using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace RoadtripPlanner;

public sealed class MapService(HttpClient http, IConfiguration config, IMemoryCache cache)
{
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
        var query = $"[out:json][timeout:25];nwr[\"tourism\"~\"^(camp_site|caravan_site)$\"](around:{radiusKm * 1000},{Number(latitude)},{Number(longitude)});out center tags;";
        using var response = await http.PostAsync(Setting("OverpassUrl"), new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        List<Campsite> results = [];
        foreach (var element in json.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (!element.TryGetProperty("tags", out var tags)) continue;
            var location = element.TryGetProperty("center", out var center) ? center : element;
            if (!location.TryGetProperty("lat", out var lat) || !location.TryGetProperty("lon", out var lon)) continue;
            if (Text(tags, "access") is "private" or "no" || Text(tags, "disused") == "yes") continue;
            var a = lat.GetDouble(); var b = lon.GetDouble();
            var distance = Distance(latitude, longitude, a, b);
            if (distance > radiusKm) continue;
            List<string> facilities = [];
            foreach (var (tag, label) in new[] { ("toilets", "Toilets"), ("shower", "Showers"), ("drinking_water", "Water"), ("power_supply", "Electricity"), ("electricity", "Electricity"), ("tents", "Tents"), ("motorhome", "Motorhomes") })
                if (Text(tags, tag) is { } value && value is not ("no" or "unknown")) facilities.Add(label);
            if (Text(tags, "internet_access") is "wlan" or "yes") facilities.Add("Wi-Fi");
            results.Add(new Campsite($"{Text(element, "type")}/{element.GetProperty("id").GetInt64()}",
                Text(tags, "name") ?? "Unnamed campsite", a, b, Math.Round(distance, 1),
                Text(tags, "tourism") ?? "camp_site", SafeWebsite(Text(tags, "website") ?? Text(tags, "contact:website")),
                Text(tags, "phone") ?? Text(tags, "contact:phone"), facilities.Distinct().ToArray()));
        }
        found = results.OrderBy(c => c.DistanceKm).Take(100).ToArray();
        cache.Set(key, found, TimeSpan.FromHours(6));
        return found;
    }

    public async Task<RoadRoute?> Route(List<RoutePoint> points, CancellationToken ct)
    {
        var coordinates = string.Join(';', points.Select(p => $"{Number(p.Longitude)},{Number(p.Latitude)}"));
        var key = "route:" + coordinates;
        if (cache.TryGetValue(key, out RoadRoute? found)) return found;
        using var json = await GetJson($"{Setting("RoutingUrl").TrimEnd('/')}/route/v1/driving/{coordinates}?overview=full&geometries=geojson&steps=false", ct);
        if (Text(json.RootElement, "code") != "Ok") return null;
        var route = json.RootElement.GetProperty("routes")[0];
        found = new RoadRoute(route.GetProperty("distance").GetDouble() / 1000,
            route.GetProperty("duration").GetDouble() / 3600,
            route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(p => new[] { p[1].GetDouble(), p[0].GetDouble() }).ToArray());
        cache.Set(key, found, TimeSpan.FromHours(6));
        return found;
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
