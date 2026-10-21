using System.Text.Json;

namespace RoadtripPlanner;

// One process owns this directory. Atomic replacement prevents partially written trips.
public sealed class TripStore
{
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TripStore(IConfiguration configuration)
    {
        directory = Path.GetFullPath(configuration["Data:Directory"] ?? "data");
        Directory.CreateDirectory(directory);
    }

    public async Task<List<Trip>> List(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            List<Trip> trips = [];
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var trip = await Read(path, ct);
                if (trip is not null) trips.Add(trip);
            }
            return trips.OrderByDescending(t => t.UpdatedAt).ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<Trip?> Get(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await Read(PathFor(id), ct); }
        finally { gate.Release(); }
    }

    public async Task<Trip> Create(Trip input, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var trip = input with { Id = Guid.NewGuid(), Name = input.Name.Trim(), Version = 1, UpdatedAt = DateTimeOffset.UtcNow };
            await Write(trip, ct);
            return trip;
        }
        finally { gate.Release(); }
    }

    public async Task<Trip?> Update(Guid id, Trip input, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = await Read(PathFor(id), ct);
            if (current is null) return null;
            if (input.Version != current.Version) throw new TripConflictException();
            var trip = input with { Id = id, Name = input.Name.Trim(), Version = current.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
            await Write(trip, ct);
            return trip;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> Delete(Guid id, int version, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = await Read(PathFor(id), ct);
            if (current is null) return false;
            if (current.Version != version) throw new TripConflictException();
            File.Delete(PathFor(id));
            return true;
        }
        finally { gate.Release(); }
    }

    private string PathFor(Guid id) => Path.Combine(directory, $"{id:D}.json");
    private static async Task<Trip?> Read(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Trip>(stream, Json, ct);
    }
    private async Task Write(Trip trip, CancellationToken ct)
    {
        var path = PathFor(trip.Id);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, trip, Json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class TripConflictException : Exception { }
