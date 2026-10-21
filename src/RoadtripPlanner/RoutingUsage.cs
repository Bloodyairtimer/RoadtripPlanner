using System.Text.Json;

namespace RoadtripPlanner;

// One app process owns the data volume. Reserve before sending, including failed attempts.
public sealed class RoutingUsage(IConfiguration config, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(Path.GetFullPath(config["Data:Directory"] ?? "data"), "routing-usage", "tomtom.json");
    public int Limit { get; } = Math.Clamp(config.GetValue("TomTom:MonthlyRequestLimit", 1000), 0, 20000);
    private string Month => clock.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<RoutingUsageStatus> Status(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var usage = await Read(ct); return new(usage.Month, usage.Requests, Limit); }
        finally { gate.Release(); }
    }
    public async Task Reserve(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var usage = await Read(ct);
            if (usage.Requests >= Limit) throw new RoutingException("The local TomTom monthly request limit has been reached. Cached routes still work. Adjust the limit in Docker configuration if needed.", 429);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, usage with { Requests = usage.Requests + 1 }, cancellationToken: ct);
                    await stream.FlushAsync(ct); stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
    private async Task<RoutingUsageStatus> Read(CancellationToken ct)
    {
        if (!File.Exists(path)) return new(Month, 0, Limit);
        await using var stream = File.OpenRead(path);
        var usage = await JsonSerializer.DeserializeAsync<RoutingUsageStatus>(stream, cancellationToken: ct)
            ?? throw new IOException("Unable to read routing usage.");
        if (usage.Requests < 0 || !DateOnly.TryParseExact(usage.Month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new IOException("Invalid routing usage.");
        return usage.Month == Month ? usage with { Limit = Limit } : new(Month, 0, Limit);
    }
}
public sealed record RoutingUsageStatus(string Month, int Requests, int Limit);
public sealed class RoutingException(string message, int statusCode = 503) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
