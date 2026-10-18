namespace RoadtripPlanner;

public sealed record Trip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public DateOnly? StartDate { get; init; }
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
    double DistanceKm, string Type, string? Website, string? Phone, string[] Facilities);
public sealed record RouteRequest(List<RoutePoint> Points);
public sealed record RoutePoint(double Latitude, double Longitude);
public sealed record RoadRoute(double DistanceKm, double DurationHours, double[][] Coordinates);

public static class TripValidation
{
    public static string? Validate(Trip trip)
    {
        if (string.IsNullOrWhiteSpace(trip.Name) || trip.Name.Length > 120)
            return "Give your trip a name of 1–120 characters.";
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
                || camp.Type is not ("camp_site" or "caravan_site") || camp.Facilities is null || camp.Facilities.Length > 30
                || camp.Facilities.Any(f => f is null || f.Length > 100)
                || (camp.Website?.Length ?? 0) > 2000 || (camp.Phone?.Length ?? 0) > 100))
                return "The selected campsite is invalid.";
        }
        return null;
    }

    public static bool ValidCoordinates(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
}
