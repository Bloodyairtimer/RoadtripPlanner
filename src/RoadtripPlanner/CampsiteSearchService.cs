using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RoadtripPlanner;

public sealed class CampsiteSearchService(MapService maps, Park4NightService park4Night)
{
    public async Task<CampsiteSearchResult> Search(double latitude,double longitude,int radiusKm,CancellationToken ct)
    {
        var osm = Query("OpenStreetMap",() => maps.Campsites(latitude,longitude,radiusKm,ct),ct);
        var park = park4Night.Enabled ? Query("Park4Night",() => park4Night.Search(latitude,longitude,radiusKm,ct),ct) : null;
        var providers = park is null ? new[] { await osm } : await Task.WhenAll(osm,park);
        if (providers.All(p => !p.Available)) throw new CampsiteSearchException(new HttpRequestException("All enabled campsite providers failed."));
        return new CampsiteSearchResult(Merge(providers.SelectMany(p => p.Campsites)),
            providers.Where(p => !p.Available).Select(p => $"{p.Source} is unavailable. Showing results from the other source; try again to refresh.").ToArray());
    }

    private static async Task<ProviderResult> Query(string source,Func<Task<Campsite[]>> search,CancellationToken ct)
    {
        try { return new(source,await search(),true); }
        catch (Exception e) when ((e is CampsiteSearchException or HttpRequestException or JsonException or OperationCanceledException) && !ct.IsCancellationRequested)
        { return new(source,[],false); }
    }

    public static Campsite[] Merge(IEnumerable<Campsite> candidates)
    {
        List<Campsite> merged = [];
        foreach (var candidate in candidates)
        {
            var index = merged.FindIndex(existing => existing.Id == candidate.Id || SamePlace(existing,candidate));
            if (index < 0) { merged.Add(candidate); continue; }
            var current = merged[index];
            var links = Links(current).Concat(Links(candidate)).DistinctBy(l => l.Url).ToArray();
            merged[index] = current with {
                Facilities = current.Facilities.Concat(candidate.Facilities).Distinct().ToArray(),
                Website = current.Website ?? candidate.Website, Phone = current.Phone ?? candidate.Phone,
                Rating = current.Rating ?? candidate.Rating, ReviewCount = current.ReviewCount ?? candidate.ReviewCount,
                Category = current.Category ?? candidate.Category,
                OtherSources = links.Where(l => l.Url != SourceUrl(current)).Take(4).ToArray()
            };
        }
        return merged.OrderBy(c => c.DistanceKm).ThenBy(c => c.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Keep nearby separate businesses separate: require compatible type, a matching name,
    // and no more than 300 m between the OSM centre and Park4Night entrance.
    private static bool SamePlace(Campsite a,Campsite b) => a.Source != b.Source && a.Type == b.Type
        && MapService.Distance(a.Latitude,a.Longitude,b.Latitude,b.Longitude) <= .3
        && NameKey(a.Name).Length >= 4 && NameKey(a.Name) == NameKey(b.Name);
    private static string NameKey(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c)) result.Append(' ');
        return string.Concat(result.ToString().Split(' ',StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("camping" or "campsite" or "campground" or "campingplatz" or "camperplaats")));
    }
    private static string SourceUrl(Campsite c) => c.SourceUrl ?? $"https://www.openstreetmap.org/{c.Id}";
    private static IEnumerable<CampsiteSourceLink> Links(Campsite c) => new[] { new CampsiteSourceLink(c.Source,SourceUrl(c)) }.Concat(c.OtherSources ?? []);
    private sealed record ProviderResult(string Source,Campsite[] Campsites,bool Available);
}
