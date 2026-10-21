using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace RoadtripPlanner;

public sealed class MapService(HttpClient http, IConfiguration config, IMemoryCache cache)
{
    private readonly SemaphoreSlim campsiteGate = new(1, 1);
    private readonly SemaphoreSlim geocodeGate = new(1, 1);
    private DateTimeOffset nextGeocode = DateTimeOffset.MinValue;
    private string Setting(string key) => config[$"Maps:{key}"] ?? throw new InvalidOperationException($"Maps:{key} is not configured.");
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public async Task<Place[]> Search(string query, CancellationToken ct)
    {
        var key = "place:" + query.Trim().ToLowerInvariant();
        if (cache.TryGetValue(key, out Place[]? found)) return found!;
        await geocodeGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out found)) return found!;
            var delay = nextGeocode - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            try
            {
                using var json = await GetJson($"{Setting("NominatimUrl").TrimEnd('/')}/search?format=jsonv2&limit=6&q={Uri.EscapeDataString(query)}", ct);
                var results = json.RootElement.EnumerateArray().Select(p => new Place(
                    Text(p, "name") ?? Text(p, "display_name")?.Split(',')[0] ?? query,
                    Text(p, "display_name") ?? query,
                    double.Parse(p.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
                    double.Parse(p.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture))).ToArray();
                cache.Set(key, results, TimeSpan.FromDays(1));
                return results;
            }
            finally { nextGeocode = DateTimeOffset.UtcNow.AddSeconds(1.1); }
        }
        finally { geocodeGate.Release(); }
    }

    public async Task<Campsite[]> Campsites(double latitude, double longitude, int radiusKm, CancellationToken ct)
    {
        var key = $"camp:{Number(latitude)}:{Number(longitude)}:{radiusKm}";
        if (cache.TryGetValue(key, out Campsite[]? found)) return found!;
        await campsiteGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out found)) return found!;
            var query = $"[out:json][timeout:12];nwr[\"tourism\"~\"^(camp_site|caravan_site)$\"](around:{radiusKm * 1000},{Number(latitude)},{Number(longitude)});out center tags;";
            var endpoints = new[] { Setting("OverpassUrl") }
                .Concat(config.GetSection("Maps:OverpassFallbackUrls").Get<string[]>() ?? [])
                .Distinct().Take(3);
            Exception? lastFailure = null;
            foreach (var endpoint in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(16));
                    // A small encoded GET is compatible with providers/proxies that reject form POSTs.
                    using var json = await GetJson($"{endpoint}{(endpoint.Contains('?') ? '&' : '?')}data={Uri.EscapeDataString(query)}", timeout.Token);
                    if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("remark", out _) || !json.RootElement.TryGetProperty("elements", out var elements)
                        || elements.ValueKind != JsonValueKind.Array)
                        throw new HttpRequestException("The campsite provider returned an incomplete response.");
                    found = ParseCampsites(elements, latitude, longitude, radiusKm);
                    cache.Set(key, found, found.Length == 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(6));
                    return found;
                }
                catch (Exception e) when ((e is HttpRequestException or JsonException or OperationCanceledException) && !ct.IsCancellationRequested)
                {
                    lastFailure = e;
                }
            }
            if (config.GetValue("Maps:EnableGeocodingCampsiteFallback", true))
            {
                try
                {
                    found = await SearchCampsitesByName(latitude, longitude, radiusKm, ct);
                    cache.Set(key, found, TimeSpan.FromMinutes(15));
                    return found;
                }
                catch (Exception e) when ((e is HttpRequestException or JsonException or OperationCanceledException) && !ct.IsCancellationRequested)
                {
                    lastFailure = e;
                }
            }
            throw new CampsiteSearchException(lastFailure);
        }
        finally { campsiteGate.Release(); }
    }

    // One bounded, explicit amenity search as a last resort, sharing the geocoder's 1 req/sec gate.
    // This returns a selection of campings, not an exhaustive area download.
    private async Task<Campsite[]> SearchCampsitesByName(double latitude, double longitude, int radiusKm, CancellationToken ct)
    {
        await geocodeGate.WaitAsync(ct);
        try
        {
            var delay = nextGeocode - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var deltaLat = radiusKm / 111.2;
                var deltaLon = Math.Min(180, deltaLat / Math.Max(.01, Math.Cos(latitude * Math.PI / 180)));
                var box = string.Join(',', new[] { Math.Max(-180,longitude-deltaLon), Math.Min(90,latitude+deltaLat), Math.Min(180,longitude+deltaLon), Math.Max(-90,latitude-deltaLat) }.Select(Number));
                using var json = await GetJson($"{Setting("NominatimUrl").TrimEnd('/')}/search?format=jsonv2&q=%5Bcamping%5D&viewbox={box}&bounded=1&limit=40&extratags=1", timeout.Token);
                List<Campsite> results = [];
                foreach (var p in json.RootElement.EnumerateArray())
                {
                    var type = Text(p,"type");
                    if (type is not ("camp_site" or "caravan_site")) continue;
                    if (!double.TryParse(Text(p,"lat"), CultureInfo.InvariantCulture, out var lat)
                        || !double.TryParse(Text(p,"lon"), CultureInfo.InvariantCulture, out var lon)
                        || !TripValidation.ValidCoordinates(lat,lon)) continue;
                    var distance = Distance(latitude,longitude,lat,lon);
                    if (distance > radiusKm) continue;
                    var hasTags = p.TryGetProperty("extratags", out var tags) && tags.ValueKind == JsonValueKind.Object;
                    var access = hasTags ? Text(tags,"access") : null;
                    if (access == "no" || (hasTags && (Text(tags,"disused") == "yes" || Text(tags,"abandoned") == "yes"))) continue;
                    var osmType = Text(p,"osm_type");
                    if (osmType is not ("node" or "way" or "relation") || !p.TryGetProperty("osm_id",out var id)) continue;
                    results.Add(new Campsite($"{osmType}/{id.GetInt64()}",Text(p,"name") ?? Text(p,"display_name")?.Split(',')[0] ?? "Unnamed camping",lat,lon,Math.Round(distance,1),type,
                        hasTags ? SafeWebsite(Text(tags,"website") ?? Text(tags,"contact:website")) : null,
                        hasTags ? Text(tags,"phone") ?? Text(tags,"contact:phone") : null,[],access));
                }
                return results.DistinctBy(c => c.Id).OrderBy(c => c.DistanceKm).ToArray();
            }
            finally { nextGeocode = DateTimeOffset.UtcNow.AddSeconds(1.1); }
        }
        finally { geocodeGate.Release(); }
    }

    private static Campsite[] ParseCampsites(JsonElement elements, double latitude, double longitude, int radiusKm)
    {
        List<Campsite> results = [];
        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("tags", out var tags)) continue;
            var location = element.TryGetProperty("center", out var center) ? center : element;
            if (!location.TryGetProperty("lat", out var lat) || !location.TryGetProperty("lon", out var lon)) continue;
            if (Text(tags, "access") == "no" || Text(tags, "disused") == "yes" || Text(tags, "abandoned") == "yes") continue;
            var a = lat.GetDouble(); var b = lon.GetDouble();
            if (!TripValidation.ValidCoordinates(a, b)) continue;
            var distance = Distance(latitude, longitude, a, b);
            if (distance > radiusKm) continue;
            List<string> facilities = [];
            foreach (var (tag, label) in new[] { ("toilets", "Toilets"), ("shower", "Showers"), ("drinking_water", "Water"), ("power_supply", "Electricity"), ("electricity", "Electricity"), ("tents", "Tents"), ("motorhome", "Motorhomes") })
                if (Text(tags, tag) is { } value && value is not ("no" or "unknown")) facilities.Add(label);
            if (Text(tags, "internet_access") is "wlan" or "yes") facilities.Add("Wi-Fi");
            results.Add(new Campsite($"{Text(element, "type")}/{element.GetProperty("id").GetInt64()}",
                Text(tags, "name") ?? "Unnamed campsite", a, b, Math.Round(distance, 1),
                Text(tags, "tourism") ?? "camp_site", SafeWebsite(Text(tags, "website") ?? Text(tags, "contact:website")),
                Text(tags, "phone") ?? Text(tags, "contact:phone"), facilities.Distinct().ToArray(), Text(tags, "access")));
        }
        return results.DistinctBy(c => c.Id).OrderBy(c => c.DistanceKm).Take(150).ToArray();
    }

    public async Task<RoadRoute?> Route(List<RoutePoint> points, CancellationToken ct)
    {
        var coordinates = string.Join(';', points.Select(p => $"{Number(p.Longitude)},{Number(p.Latitude)}"));
        var key = "route:stopovers:" + coordinates;
        if (cache.TryGetValue(key, out RoadRoute? found)) return found;
        // Stops are visits, not through-waypoints: allow departure in either direction.
        using var json = await GetJson($"{Setting("RoutingUrl").TrimEnd('/')}/route/v1/driving/{coordinates}?overview=full&geometries=geojson&steps=true&continue_straight=false", ct);
        if (Text(json.RootElement, "code") != "Ok") return null;
        var route = json.RootElement.GetProperty("routes")[0];
        var providerLegs = route.GetProperty("legs").EnumerateArray().ToArray();
        if (providerLegs.Length != points.Count - 1) throw new HttpRequestException("The routing provider returned incomplete legs.");
        List<RoadLeg> legs = [];
        for (var i = 0; i < providerLegs.Length; i++)
        {
            var leg = providerLegs[i];
            var geometry = leg.GetProperty("steps").EnumerateArray()
                .SelectMany(step => step.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())
                .Select(p => new[] { p[1].GetDouble(), p[0].GetDouble() }).ToArray();
            if (geometry.Length == 0) geometry = [[points[i].Latitude, points[i].Longitude], [points[i + 1].Latitude, points[i + 1].Longitude]];
            legs.Add(new RoadLeg(leg.GetProperty("distance").GetDouble() / 1000,
                leg.GetProperty("duration").GetDouble() / 3600, geometry, Midpoint(geometry)));
        }
        found = new RoadRoute(route.GetProperty("distance").GetDouble() / 1000,
            route.GetProperty("duration").GetDouble() / 3600,
            route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(p => new[] { p[1].GetDouble(), p[0].GetDouble() }).ToArray(), legs.ToArray());
        cache.Set(key, found, TimeSpan.FromHours(6));
        return found;
    }

    // Follow the routed road geometry, rather than drawing a midpoint between destinations.
    public static RoutePoint Midpoint(double[][] coordinates)
    {
        var lengths = coordinates.Zip(coordinates.Skip(1), (a, b) => Distance(a[0], a[1], b[0], b[1])).ToArray();
        var halfway = lengths.Sum() / 2;
        var travelled = 0d;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (travelled + lengths[i] >= halfway)
            {
                var fraction = lengths[i] == 0 ? 0 : (halfway - travelled) / lengths[i];
                var a = coordinates[i]; var b = coordinates[i + 1];
                var longitudeDelta = (b[1] - a[1] + 540) % 360 - 180;
                return new RoutePoint(a[0] + (b[0] - a[0]) * fraction, (a[1] + longitudeDelta * fraction + 540) % 360 - 180);
            }
            travelled += lengths[i];
        }
        return new RoutePoint(coordinates[^1][0], coordinates[^1][1]);
    }

    private async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static string? SafeWebsite(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2)
            + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians) * Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}

public sealed class CampsiteSearchException(Exception? inner) : Exception("No campsite provider is available.", inner) { }
