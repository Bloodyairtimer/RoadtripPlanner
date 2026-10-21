using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace RoadtripPlanner;

public sealed class TomTomRoutingService(HttpClient http, IConfiguration config, IMemoryCache cache, RoutingUsage usage)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string apiKey = config["TomTom:ApiKey"]?.Trim() ?? "";
    public bool Configured => apiKey.Length > 0;
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public async Task<RoadRoute> Route(List<RoutePoint> points, CamperProfile camper, CancellationToken ct)
    {
        if (!Configured) throw new RoutingException("Camper routing needs a TomTom API key. Set TOMTOM_API_KEY in your local .env file and restart Docker, or choose standard car routing in Trip settings.");
        var input = JsonSerializer.Serialize(new { points, camper });
        var key = "tomtom:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        if (cache.TryGetValue(key, out RoadRoute? cached)) return cached!;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached!;
            ct.ThrowIfCancellationRequested();
            await usage.Reserve(ct);
            var locations = string.Join(':', points.Select(p => $"{Number(p.Latitude)},{Number(p.Longitude)}"));
            // A private <=3.5t motorhome: retain car access rules with physical restrictions.
            // A truck mode could impose truck-only restrictions and speed assumptions.
            var url = $"https://api.tomtom.com/routing/1/calculateRoute/{locations}/json?key={Uri.EscapeDataString(apiKey)}"
                + $"&travelMode=car&vehicleCommercial=false&vehicleWeight={camper.WeightKg}&vehicleLength={Number(camper.LengthM)}"
                + $"&vehicleWidth={Number(camper.WidthM)}&vehicleHeight={Number(camper.HeightM)}&vehicleMaxSpeed={camper.MaxSpeedKmh}"
                + "&routeType=fastest&traffic=false&computeBestOrder=false&routeRepresentation=polyline&avoid=unpavedRoads";
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                    throw new RoutingException(response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "TomTom rejected the API key or Routing API access. Check your local API key and TomTom account permissions.",
                        System.Net.HttpStatusCode.TooManyRequests => "TomTom's request limit has been reached. Try again later.",
                        System.Net.HttpStatusCode.BadRequest => "TomTom could not plan this camper route. Check the locations and vehicle dimensions.",
                        _ => "TomTom routing is unavailable. Try again later."
                    });
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!json.RootElement.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
                    throw new RoutingException("TomTom found no route for these locations and camper dimensions.", 422);
                var route = routes[0];
                var legs = route.GetProperty("legs").EnumerateArray().Select(leg =>
                {
                    var geometry = leg.GetProperty("points").EnumerateArray().Select(p => new[] { p.GetProperty("latitude").GetDouble(), p.GetProperty("longitude").GetDouble() }).ToArray();
                    if (geometry.Length < 2 || geometry.Any(p => !TripValidation.ValidCoordinates(p[0], p[1])))
                        throw new RoutingException("TomTom returned incomplete road geometry. Retry the route.");
                    var summary = leg.GetProperty("summary");
                    var distance = summary.GetProperty("lengthInMeters").GetDouble() / 1000;
                    var hours = summary.GetProperty("travelTimeInSeconds").GetDouble() / 3600;
                    if (!double.IsFinite(distance) || !double.IsFinite(hours) || distance < 0 || hours < 0)
                        throw new RoutingException("TomTom returned invalid driving estimates. Retry the route.");
                    return new RoadLeg(distance, hours, geometry, MapService.Midpoint(geometry));
                }).ToArray();
                if (legs.Length != points.Count - 1) throw new RoutingException("TomTom returned incomplete route legs. Retry the route.");
                var found = new RoadRoute(legs.Sum(l => l.DistanceKm), legs.Sum(l => l.DurationHours), legs.SelectMany(l => l.Coordinates).ToArray(), legs, "TomTom");
                cache.Set(key, found, TimeSpan.FromHours(6));
                return found;
            }
            // Never expose the request URI (contains the key) or provider error bodies.
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
            { throw new RoutingException("TomTom routing could not be reached. Try again later."); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new RoutingException("TomTom returned an invalid route response. Try again later."); }
        }
        finally { gate.Release(); }
    }
}
