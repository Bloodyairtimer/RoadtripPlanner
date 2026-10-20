using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace RoadtripPlanner;

// Adapter for the unofficial guest endpoint documented by gtoselli/park4night-api.
// One nearby request per centre; no pagination, grid sampling or account impersonation.
public sealed class Park4NightService(HttpClient http, IConfiguration config, IMemoryCache cache)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest = DateTimeOffset.MinValue;
    public bool Enabled => config.GetValue("Park4Night:Enabled", true);

    public async Task<Campsite[]> Search(double latitude, double longitude, int radiusKm, CancellationToken ct)
    {
        if (!Enabled) return [];
        var centre = string.Join(':', Number(latitude), Number(longitude));
        var key = "park4night:" + centre;
        if (!cache.TryGetValue(key, out Campsite[]? results))
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!cache.TryGetValue(key, out results))
                {
                    var delay = nextRequest - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        var endpoint = config["Park4Night:SearchUrl"] ?? "https://guest.park4night.com/services/V4.1/lieuxGetFilter.php";
                        var url = $"{endpoint}{(endpoint.Contains('?') ? '&' : '?')}latitude={Number(latitude)}&longitude={Number(longitude)}";
                        using var response = await http.GetAsync(url, timeout.Token);
                        response.EnsureSuccessStatusCode();
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        results = Parse(json.RootElement, latitude, longitude);
                        cache.Set(key, results, results.Length == 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15));
                    }
                    finally { nextRequest = DateTimeOffset.UtcNow.AddSeconds(1); }
                }
            }
            finally { gate.Release(); }
        }
        // The guest API chooses its own nearest-result limit; filter the actual radius locally.
        return results!.Where(c => MapService.Distance(latitude, longitude, c.Latitude, c.Longitude) <= radiusKm).ToArray();
    }

    private static Campsite[] Parse(JsonElement root, double latitude, double longitude)
    {
        JsonElement rows;
        if (root.ValueKind == JsonValueKind.Array) rows = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lieux", out rows)
                 && rows.ValueKind == JsonValueKind.Array)
        {
            if (Text(root,"status") is { } status && !status.Equals("OK",StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Park4Night returned an unsuccessful search status.");
        }
        else throw new HttpRequestException("Park4Night returned an unsupported response.");
        List<Campsite> camps = [];
        foreach (var row in rows.EnumerateArray().Take(1000))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!long.TryParse(Text(row,"id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            if (!TryNumber(row,"latitude",out var lat) || !TryNumber(row,"longitude",out var lon)
                || !TripValidation.ValidCoordinates(lat,lon)) continue;
            var category = Category(Text(row,"code"));
            if (category is null) continue;
            if (row.TryGetProperty("publique",out _) && !Yes(row,"publique")) continue;
            List<string> facilities = [];
            foreach (var (field,label) in new[] { ("point_eau","Water"),("wc_public","Toilets"),("douche","Showers"),
                ("electricite","Electricity"),("wifi","Wi-Fi"),("piscine","Pool"),("laverie","Laundry"),
                ("eau_noire","Chemical disposal"),("eau_usee","Grey water"),("animaux","Pets allowed") })
                if (Yes(row,field)) facilities.Add(label);
            var reviews = TryNumber(row,"nb_commentaires",out var count) && count is >= 0 and <= int.MaxValue ? (int)count : (int?)null;
            var rating = TryNumber(row,"note_moyenne",out var score) && score is >= 0 and <= 5 && reviews > 0 ? score : (double?)null;
            var name = NonBlank(Text(row,"name")) ?? NonBlank(Text(row,"titre")) ?? "Unnamed Park4Night place";
            camps.Add(new Campsite($"park4night/{id}", name[..Math.Min(name.Length,500)], lat, lon,
                Math.Round(MapService.Distance(latitude,longitude,lat,lon),1), category.Value.Type,
                MapService.SafeWebsite(Text(row,"site_internet")), Text(row,"tel"), facilities.ToArray(),
                Source:"Park4Night", SourceUrl:$"https://park4night.com/en/place/{id}", Rating:rating,
                ReviewCount:reviews, Category:category.Value.Label));
        }
        return camps.DistinctBy(c => c.Id).OrderBy(c => c.DistanceKm).ToArray();
    }

    private static (string Type,string Label)? Category(string? code) => code?.ToUpperInvariant() switch
    {
        "C" => ("camp_site","Camping"),
        "ACC_G" => ("caravan_site","Free motorhome area"),
        "ACC_P" => ("caravan_site","Paid motorhome area"),
        "ACC_PR" => ("caravan_site","Private motorhome area"),
        "PSS" => ("caravan_site","Motorhome parking without services"),
        "F" => ("caravan_site","Farm stay"),
        "P" => ("overnight_parking","Parking day/night"),
        "PN" => ("nature_spot","Surrounded by nature"),
        // PJ = day-only parking, APN = picnic area, DS = services, AR = rest area.
        // Unknown categories are not silently advertised as overnight accommodation.
        _ => null
    };
    private static string Number(double value) => value.ToString("0.######",CultureInfo.InvariantCulture);
    private static string? Text(JsonElement row,string field) => row.TryGetProperty(field,out var value) ? value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null
    } : null;
    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static bool TryNumber(JsonElement row,string field,out double value) =>
        double.TryParse(Text(row,field),NumberStyles.Float,CultureInfo.InvariantCulture,out value) && double.IsFinite(value);
    private static bool Yes(JsonElement row,string field) => row.TryGetProperty(field,out var value)
        && (value.ValueKind == JsonValueKind.True || Text(row,field) is "1" or "yes" or "true");
}
