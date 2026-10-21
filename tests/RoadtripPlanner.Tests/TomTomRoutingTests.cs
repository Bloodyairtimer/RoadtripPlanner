using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using RoadtripPlanner;
using Xunit;

namespace RoadtripPlanner.Tests;

public sealed class TomTomRoutingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private const string Key = "local-test-secret-do-not-expose";
    private const string Fixture = """
    {"routes":[{"legs":[
      {"summary":{"lengthInMeters":175000,"travelTimeInSeconds":7200},"points":[{"latitude":52,"longitude":5},{"latitude":50,"longitude":7},{"latitude":48,"longitude":8}]},
      {"summary":{"lengthInMeters":100000,"travelTimeInSeconds":3600},"points":[{"latitude":48,"longitude":8},{"latitude":49,"longitude":6},{"latitude":52,"longitude":5}]}
    ]}]}
    """;
    private static readonly List<RoutePoint> Points = [new(52,5),new(48,8),new(52,5)];
    private IConfiguration Settings(string key = Key, int limit = 1000) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Data:Directory"] = directory,["TomTom:ApiKey"] = key,["TomTom:MonthlyRequestLimit"] = limit.ToString()
    }).Build();

    [Fact]
    public async Task CamperParametersGeometryTotalsAndConcurrentCacheAreCorrect()
    {
        var settings = Settings(); var handler = new FixtureHandler(Fixture);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var usage = new RoutingUsage(settings,TimeProvider.System);
        var service = new TomTomRoutingService(http,settings,cache,usage);
        var routes = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => service.Route(Points,new CamperProfile(),CancellationToken.None)));
        var route = routes[0];
        Assert.Equal("TomTom",route.Provider); Assert.Equal(275,route.DistanceKm); Assert.Equal(3,route.DurationHours);
        Assert.Equal(2,route.Legs.Length); Assert.Equal(new double[] {49,6},route.Legs[1].Coordinates[1]);
        Assert.Equal(new double[] {52,5},route.Coordinates[^1]);
        Assert.InRange(route.Legs[1].Midpoint.Latitude,48,52);
        var uri = Uri.UnescapeDataString(handler.LastUri);
        foreach (var parameter in new[] {"52,5:48,8:52,5","travelMode=car","vehicleCommercial=false","vehicleWeight=3500","vehicleLength=7.48","vehicleWidth=2.3","vehicleHeight=2.98","vehicleMaxSpeed=120","computeBestOrder=false","traffic=false","avoid=unpavedRoads"}) Assert.Contains(parameter,uri);
        Assert.Equal(1,handler.Calls); Assert.Equal(1,(await usage.Status(default)).Requests);
        await service.Route(Points,new CamperProfile { MaxSpeedKmh = 100 },default);
        await service.Route(Points,new CamperProfile { HeightM = 3.1 },default);
        Assert.Equal(3,handler.Calls); Assert.Equal(3,(await usage.Status(default)).Requests);
    }

    [Fact]
    public async Task MissingKeyAndCancellationDoNotSendOrConsumeQuota()
    {
        var settings = Settings(""); var handler = new FixtureHandler(Fixture);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var usage = new RoutingUsage(settings,TimeProvider.System);
        var missing = new TomTomRoutingService(http,settings,cache,usage);
        Assert.False(missing.Configured);
        await Assert.ThrowsAsync<RoutingException>(()=>missing.Route(Points,new(),default));
        var configured = new TomTomRoutingService(http,Settings(),cache,usage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>configured.Route(Points,new(),new CancellationToken(true)));
        Assert.Equal(0,handler.Calls); Assert.Equal(0,(await usage.Status(default)).Requests);
    }

    [Fact]
    public async Task QuotaSurvivesRestartResetsAtUtcMonthAndCachedRoutesRemainAvailable()
    {
        var settings = Settings(limit:1); var clock = new TestClock();
        var handler = new FixtureHandler(Fixture);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var usage = new RoutingUsage(settings,clock);
        var service = new TomTomRoutingService(http,settings,cache,usage);
        await service.Route(Points,new(),default);
        await service.Route(Points,new(),default);
        var restart = new RoutingUsage(settings,clock);
        Assert.Equal(1,(await restart.Status(default)).Requests);
        var error = await Assert.ThrowsAsync<RoutingException>(()=>new TomTomRoutingService(http,settings,cache,restart).Route(Points,new() { MaxSpeedKmh = 100 },default));
        Assert.Equal(429,error.StatusCode); Assert.Equal(1,handler.Calls);
        clock.Now = new DateTimeOffset(2027,1,1,0,0,0,TimeSpan.Zero);
        Assert.Equal(0,(await restart.Status(default)).Requests);
        await restart.Reserve(default);
        Assert.Equal("2027-01",(await restart.Status(default)).Month);
        Assert.Equal(1,(await restart.Status(default)).Requests);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(500)]
    public async Task ProviderErrorsAreSanitizedNotCachedAndStillCountAttempts(int code)
    {
        var settings = Settings(); var usage = new RoutingUsage(settings,TimeProvider.System);
        var handler = new ScriptedHandler((_,_)=>new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(Key) });
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TomTomRoutingService(http,settings,cache,usage);
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<RoutingException>(()=>service.Route(Points,new(),default));
            Assert.DoesNotContain(Key,error.ToString());
        }
        Assert.Equal(2,handler.Requests.Count); Assert.Equal(2,(await usage.Status(default)).Requests);
    }

    [Theory]
    [InlineData("{\"routes\":[]}")]
    [InlineData("{\"routes\":[{\"legs\":[]}]}")]
    [InlineData("{\"routes\":[{\"legs\":[{\"points\":[{\"latitude\":91,\"longitude\":5}]}]}]}")]
    public async Task IncompleteOrInvalidGeometryDoesNotBecomeASuccessfulRoute(string json)
    {
        var settings = Settings(); var handler = new FixtureHandler(json);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TomTomRoutingService(http,settings,cache,new RoutingUsage(settings,TimeProvider.System));
        await Assert.ThrowsAsync<RoutingException>(()=>service.Route(Points,new(),default));
        await Assert.ThrowsAsync<RoutingException>(()=>service.Route(Points,new(),default));
        Assert.Equal(2,handler.Calls);
    }

    [Fact]
    public async Task CorruptQuotaFileFailsClosedAndZeroLimitDisablesRequests()
    {
        var usage = new RoutingUsage(Settings(limit:0),TimeProvider.System);
        await Assert.ThrowsAsync<RoutingException>(()=>usage.Reserve(default));
        var folder = Path.Combine(directory,"routing-usage"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,"tomtom.json"),"broken");
        await Assert.ThrowsAsync<JsonException>(()=>new RoutingUsage(Settings(),TimeProvider.System).Reserve(default));
        await File.WriteAllTextAsync(Path.Combine(folder,"tomtom.json"),"{}");
        await Assert.ThrowsAsync<IOException>(()=>new RoutingUsage(Settings(),TimeProvider.System).Reserve(default));
    }

    [Fact]
    public async Task ApiDoesNotExposeKeyAndDoesNotSilentlyFallbackForCamperRoutes()
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/routing/status");
        Assert.False(status.GetProperty("tomTomConfigured").GetBoolean());
        Assert.False(status.TryGetProperty("apiKey",out _));
        var response = await client.PostAsJsonAsync("/api/route",new RouteRequest(Points,new()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Contains("API key",await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/route",new RouteRequest(Points,new() { WeightKg = 4000 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/trips",new Trip { Name = "Too fast", Camper = new() { MaxSpeedKmh = 0 } })).StatusCode);
    }

    [Fact]
    public async Task NetworkFailuresNeverExposeTheKeyInExceptionDetails()
    {
        var settings = Settings();
        var handler = new ScriptedHandler((request,_)=>throw new HttpRequestException(request.RequestUri!.AbsoluteUri));
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var usage = new RoutingUsage(settings,TimeProvider.System);
        var service = new TomTomRoutingService(http,settings,cache,usage);
        var error = await Assert.ThrowsAsync<RoutingException>(()=>service.Route(Points,new(),default));
        Assert.DoesNotContain(Key,error.ToString()); Assert.Null(error.InnerException);
        Assert.Equal(1,(await usage.Status(default)).Requests);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026,12,31,23,59,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
