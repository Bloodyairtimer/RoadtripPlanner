namespace RoadtripPlanner;

public sealed record Trip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public CamperProfile? Camper { get; init; }
    public Place? StartLocation { get; init; }
    public Place? EndLocation { get; init; }
    public string Notes { get; init; } = "";
    public List<Stop> Stops { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Stop
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Nights { get; init; } = 1;
    public string Notes { get; init; } = "";
    public Campsite? Campsite { get; init; }
}

public sealed record Place(string Name, string Description, double Latitude, double Longitude);
public sealed record Campsite(string Id, string Name, double Latitude, double Longitude,
    double DistanceKm, string Type, string? Website, string? Phone, string[] Facilities, string? Access = null,
    string Source = "OpenStreetMap", string? SourceUrl = null, double? Rating = null,
    int? ReviewCount = null, string? Category = null, CampsiteSourceLink[]? OtherSources = null);
public sealed record CampsiteSourceLink(string Name,string Url);
public sealed record CampsiteSearchResult(Campsite[] Campsites,string[] Warnings);
public sealed record CamperProfile
{
    public double LengthM { get; init; } = 7.48;
    public double WidthM { get; init; } = 2.30;
    public double HeightM { get; init; } = 2.98;
    public int WeightKg { get; init; } = 3500;
    public int MaxSpeedKmh { get; init; } = 120;
}
public sealed record RouteRequest(List<RoutePoint> Points, CamperProfile? Camper = null);
public sealed record RoutePoint(double Latitude, double Longitude);
public sealed record RoadRoute(double DistanceKm, double DurationHours, double[][] Coordinates, RoadLeg[] Legs, string Provider = "OSRM");
public sealed record RoadLeg(double DistanceKm, double DurationHours, double[][] Coordinates, RoutePoint Midpoint);

public static class TripValidation
{
    public static string? Validate(Trip trip)
    {
        if (string.IsNullOrWhiteSpace(trip.Name) || trip.Name.Length > 120)
            return "Give your trip a name of 1–120 characters.";
        if (trip.StartDate.HasValue && trip.EndDate.HasValue && trip.EndDate < trip.StartDate)
            return "End date must be on or after the start date.";
        if (ValidateCamper(trip.Camper) is { } camperError) return camperError;
        foreach (var endpoint in new[] { trip.StartLocation, trip.EndLocation })
            if (endpoint is not null && (string.IsNullOrWhiteSpace(endpoint.Name) || endpoint.Name.Length > 200
                || (endpoint.Description?.Length ?? 0) > 1000 || !ValidCoordinates(endpoint.Latitude, endpoint.Longitude)))
                return "Start and end locations need a name and valid coordinates.";
        if (trip.Notes is null || trip.Notes.Length > 10000) return "Trip notes can contain up to 10,000 characters.";
        if (trip.Stops is null || trip.Stops.Count > 100) return "A trip can have up to 100 stops.";
        if (trip.Stops.Any(s => s is null)) return "Stops cannot be empty.";
        if (trip.Stops.Select(s => s.Id).Distinct().Count() != trip.Stops.Count || trip.Stops.Any(s => s.Id == Guid.Empty))
            return "Each stop needs a unique ID.";
        foreach (var stop in trip.Stops)
        {
            if (string.IsNullOrWhiteSpace(stop.Name) || stop.Name.Length > 200) return "Each stop needs a name of 1–200 characters.";
            if (!ValidCoordinates(stop.Latitude, stop.Longitude)) return "A stop has invalid coordinates.";
            if (stop.Nights is < 0 or > 365) return "Nights must be between 0 and 365.";
            if (stop.Notes is null || stop.Notes.Length > 5000) return "Stop notes can contain up to 5,000 characters.";
            if (stop.Campsite is { } camp && (!ValidCoordinates(camp.Latitude, camp.Longitude)
                || string.IsNullOrWhiteSpace(camp.Name) || camp.Name.Length > 500
                || camp.Id is null || camp.Id.Length > 200 || !double.IsFinite(camp.DistanceKm) || camp.DistanceKm < 0
                || camp.Type is not ("camp_site" or "caravan_site" or "overnight_parking" or "nature_spot") || camp.Facilities is null || camp.Facilities.Length > 30
                || camp.Facilities.Any(f => f is null || f.Length > 100)
                || (camp.Website?.Length ?? 0) > 2000 || (camp.Phone?.Length ?? 0) > 100 || (camp.Access?.Length ?? 0) > 100
                || camp.Source is not ("OpenStreetMap" or "Park4Night")
                || (camp.SourceUrl is not null && (camp.SourceUrl.Length > 2000 || MapService.SafeWebsite(camp.SourceUrl) is null))
                || (camp.Rating.HasValue && (!double.IsFinite(camp.Rating.Value) || camp.Rating.Value is < 0 or > 5))
                || camp.ReviewCount < 0 || (camp.Category?.Length ?? 0) > 120
                || (camp.OtherSources is not null && (camp.OtherSources.Length > 4
                    || camp.OtherSources.Any(link => link is null || link.Name is not ("OpenStreetMap" or "Park4Night")
                        || link.Url is null || link.Url.Length > 2000 || MapService.SafeWebsite(link.Url) is null)))))
                return "The selected campsite is invalid.";
        }
        return null;
    }

    public static string? ValidateCamper(CamperProfile? camper) => camper is not null &&
        (!double.IsFinite(camper.LengthM) || camper.LengthM is < 2 or > 15
        || !double.IsFinite(camper.WidthM) || camper.WidthM is < 1 or > 3
        || !double.IsFinite(camper.HeightM) || camper.HeightM is < 1 or > 4.5
        || camper.WeightKg is < 500 or > 3500 || camper.MaxSpeedKmh is < 30 or > 130)
        ? "Use valid camper dimensions, a weight of 500–3,500 kg and a speed limit of 30–130 km/h. Heavy motorhomes above 3,500 kg are not supported by this profile." : null;

    public static bool ValidCoordinates(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
}
