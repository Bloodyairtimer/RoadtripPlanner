using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using RoadtripPlanner;
using Xunit;

namespace RoadtripPlanner.Tests;

public sealed class PlannerTests
{
    [Fact]
    public async Task TripLifecyclePersistsReorderingNightsAndCampsiteAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Guid id;
            using (var factory = new PlannerFactory(directory))
            using (var client = factory.CreateClient())
            {
                var input = new Trip { Name = "Coastal detour", StartDate = new DateOnly(2027,4,24), StartLocation = new Place("Home","Ermelo",52.3,5.6), EndLocation = new Place("Home","Ermelo",52.3,5.6), Stops = [
                    new Stop { Name = "Milan", Latitude = 45.46, Longitude = 9.19, Nights = 2 },
                    new Stop { Name = "Nice", Latitude = 43.7, Longitude = 7.27, Nights = 3 }] };
                var createdResponse = await client.PostAsJsonAsync("/api/trips", input);
                Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                var created = (await createdResponse.Content.ReadFromJsonAsync<Trip>())!;
                id = created.Id;
                var camp = new Campsite("node/123", "Coastal camping", 43.71, 7.28, 1.4, "camp_site", "https://example.com/", null, ["Showers"]);
                var update = created with { Notes = "Take the scenic road", Stops = [created.Stops[1] with { Nights = 4, Campsite = camp },created.Stops[0]] };
                var savedResponse = await client.PutAsJsonAsync($"/api/trips/{id}", update);
                Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
                var saved = (await savedResponse.Content.ReadFromJsonAsync<Trip>())!;
                Assert.Equal(2, saved.Version);
                Assert.Equal(new[] { "Nice", "Milan" }, saved.Stops.Select(s => s.Name));
                Assert.Equal("Coastal camping", saved.Stops[0].Campsite?.Name);
                Assert.Equal(4, saved.Stops[0].Nights);
                Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/trips/{id}", created)).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/trips/{id}?version=1")).StatusCode);
            }
            using (var factory = new PlannerFactory(directory))
            using (var client = factory.CreateClient())
            {
                var trip = await client.GetFromJsonAsync<Trip>($"/api/trips/{id}");
                Assert.Equal("Take the scenic road", trip!.Notes);
                Assert.Equal("Home", trip.StartLocation?.Name);
                Assert.Equal(52.3, trip.EndLocation?.Latitude);
                Assert.Equal("Nice", trip.Stops[0].Name);
                Assert.Single((await client.GetFromJsonAsync<Trip[]>("/api/trips"))!);
                var copyResponse = await client.PostAsJsonAsync("/api/trips", trip with { Name = "A second trip" });
                var copy = (await copyResponse.Content.ReadFromJsonAsync<Trip>())!;
                Assert.NotEqual(trip.Id, copy.Id);
                Assert.Equal(2, (await client.GetFromJsonAsync<Trip[]>("/api/trips"))!.Length);
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/trips/{id}?version=2")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/trips/{id}")).StatusCode);
                Assert.NotNull(await client.GetFromJsonAsync<Trip>($"/api/trips/{copy.Id}"));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("", 0, 0, 1)]
    [InlineData("Invalid location", 91, 0, 1)]
    [InlineData("Invalid longitude", 0, -181, 1)]
    [InlineData("Negative nights", 0, 0, -1)]
    [InlineData("Too many nights", 0, 0, 366)]
    public async Task RejectsInvalidTrips(string name, double latitude, double longitude, int nights)
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        var trip = new Trip { Name = name, Stops = [new Stop { Name = "Stop", Latitude = latitude, Longitude = longitude, Nights = nights }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trips",trip)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Trip[]>("/api/trips"))!);
    }

    [Fact]
    public async Task RejectsDuplicateStopIdsAndCrossOriginWrites()
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        var stop = new Stop { Name = "Stop" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trips",new Trip { Name = "Trip", Stops = [stop,stop] })).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://other-site.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/trips",new Trip { Name = "Trip" })).StatusCode);
    }

    [Theory]
    [InlineData("/api/campsites?latitude=91&longitude=5&radiusKm=20")]
    [InlineData("/api/campsites?latitude=52&longitude=5&radiusKm=51")]
    [InlineData("/api/places?q=a")]
    public async Task InvalidSearchDoesNotCallProviders(string path)
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task SimultaneousUpdatesDetectConflictsWithoutCorruptingFile()
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        var created = (await (await client.PostAsJsonAsync("/api/trips",new Trip { Name = "Original" })).Content.ReadFromJsonAsync<Trip>())!;
        var updates = await Task.WhenAll(Enumerable.Range(0,8).Select(i => client.PutAsJsonAsync($"/api/trips/{created.Id}",created with { Name = $"Edit {i}" })));
        Assert.Single(updates, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7,updates.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2,(await client.GetFromJsonAsync<Trip>($"/api/trips/{created.Id}"))!.Version);
    }

    [Fact]
    public async Task CampsiteProviderParsesCentersSortsFiltersAndCaches()
    {
        const string json = """
        {"elements":[
          {"type":"way","id":1,"center":{"lat":52.01,"lon":5},"tags":{"tourism":"camp_site","name":"Forest camp","website":"javascript:bad()","shower":"yes","power_supply":"yes"}},
          {"type":"node","id":2,"lat":52.001,"lon":5,"tags":{"tourism":"caravan_site","name":"Camper stop","website":"https://example.com"}},
          {"type":"node","id":3,"lat":52,"lon":5,"tags":{"tourism":"camp_site","name":"Closed","access":"no"}},
          {"type":"node","id":4,"lat":53,"lon":5,"tags":{"tourism":"camp_site","name":"Too far"}}
        ]}
        """;
        var handler = new FixtureHandler(json);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var maps = new MapService(http, Settings(), cache);
        var camps = await maps.Campsites(52,5,20,CancellationToken.None);
        Assert.Equal(new[] { "Camper stop", "Forest camp" },camps.Select(c => c.Name));
        Assert.Null(camps[1].Website);
        Assert.Contains("Showers",camps[1].Facilities);
        Assert.Contains("Electricity",camps[1].Facilities);
        Assert.Equal("way/1",camps[1].Id);
        await maps.Campsites(52,5,20,CancellationToken.None);
        Assert.Equal(1,handler.Calls);
        Assert.Contains("camp_site", Uri.UnescapeDataString(handler.LastUri));
    }

    [Fact]
    public async Task GeocodingEncodesQueryAndCachesResults()
    {
        var handler = new FixtureHandler("""[{"name":"Nice","display_name":"Nice, France","lat":"43.7","lon":"7.27"}]""");
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var maps = new MapService(http,Settings(),cache);
        var places = await maps.Search("Nice & coast",CancellationToken.None);
        Assert.Equal(43.7,Assert.Single(places).Latitude);
        Assert.Contains("q=Nice%20%26%20coast",handler.LastUri);
        await maps.Search("nice & coast",CancellationToken.None);
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task RoutingConvertsGeoJsonIntoMapCoordinates()
    {
        var handler = new FixtureHandler("""{"code":"Ok","routes":[{"distance":100000,"duration":7200,"geometry":{"coordinates":[[5,52],[6,50],[7,43]]},"legs":[{"distance":100000,"duration":7200,"steps":[{"geometry":{"coordinates":[[5,52],[6,50],[7,43]]}}]}]}]}""");
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var maps = new MapService(http,Settings(),cache);
        var route = await maps.Route([new(52,5),new(43,7)],CancellationToken.None);
        Assert.NotNull(route);
        Assert.Equal(100,route.DistanceKm);
        Assert.Equal(2,route.DurationHours);
        Assert.Equal(new double[] {52,5},route.Coordinates[0]);
        var leg = Assert.Single(route.Legs);
        Assert.Equal(100,leg.DistanceKm);
        Assert.Equal(2,leg.DurationHours);
        Assert.InRange(leg.Midpoint.Latitude,43,50);
        Assert.InRange(leg.Midpoint.Longitude,6,7);
    }

    [Fact]
    public void DistanceWorksAcrossInternationalDateLine()
    {
        Assert.InRange(MapService.Distance(0,179.9,0,-179.9),22,23);
        Assert.Equal(0,MapService.Distance(52,5,52,5));
        Assert.False(TripValidation.ValidCoordinates(double.NaN,5));
        Assert.Null(MapService.SafeWebsite("javascript:alert(1)"));
    }

    [Fact]
    public async Task RejectsInvalidEndpointAndAcceptsLegacyTripWithoutEndpoints()
    {
        using var factory = new PlannerFactory(); using var client = factory.CreateClient();
        var invalid = new Trip { Name = "Invalid home", StartLocation = new Place("Home","",91,5) };
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/trips",invalid)).StatusCode);
        using var content = new StringContent("{\"name\":\"Old trip\",\"stops\":[],\"notes\":\"\"}",System.Text.Encoding.UTF8,"application/json");
        var response = await client.PostAsync("/api/trips",content);
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var trip = await response.Content.ReadFromJsonAsync<Trip>();
        Assert.Null(trip!.StartLocation);
        Assert.Null(trip.EndLocation);
    }

    [Theory]
    [InlineData(503, "Busy")]
    [InlineData(200, "{\"remark\":\"runtime error: timeout\",\"elements\":[]}")]
    [InlineData(200, "<html>Gateway problem</html>")]
    [InlineData(200, "[]")]
    public async Task CampsiteSearchFallsBackOnBusyIncompleteOrMalformedProvider(int status,string firstBody)
    {
        var handler = new ScriptedHandler((_,call) => call == 1
            ? Reply(firstBody,(HttpStatusCode)status)
            : Reply("{\"elements\":[{\"type\":\"node\",\"id\":123,\"lat\":52.001,\"lon\":5,\"tags\":{\"tourism\":\"camp_site\",\"name\":\"Available camping\",\"access\":\"private\"}}]}"));
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = WithFallback();
        var maps = new MapService(http,config,cache);
        var camps = await maps.Campsites(52,5,20,CancellationToken.None);
        Assert.Equal("Available camping",Assert.Single(camps).Name);
        Assert.Equal("private",camps[0].Access);
        Assert.Equal(2,handler.Requests.Count);
        Assert.Contains("backup.example",handler.Requests[1]);
        await maps.Campsites(52,5,20,CancellationToken.None);
        Assert.Equal(2,handler.Requests.Count);
    }

    [Fact]
    public async Task AllOverpassFailuresUseOneBoundedGeocoderSearch()
    {
        var handler = new ScriptedHandler((request,_) => request.RequestUri!.Host == "geo.example"
            ? Reply("[{\"osm_type\":\"way\",\"osm_id\":77,\"type\":\"camp_site\",\"name\":\"Sunny camping\",\"lat\":\"52.01\",\"lon\":\"5.01\",\"extratags\":{\"website\":\"https://example.com\"}},{\"osm_type\":\"node\",\"osm_id\":78,\"type\":\"shop\",\"name\":\"Camping shop\",\"lat\":\"52.0\",\"lon\":\"5.0\"}]")
            : Reply("Busy",HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var maps = new MapService(http,WithFallback(),cache);
        var camp = Assert.Single(await maps.Campsites(52,5,20,CancellationToken.None));
        Assert.Equal("Sunny camping",camp.Name);
        Assert.Equal("way/77",camp.Id);
        Assert.Contains("bounded=1",handler.Requests[^1]);
        Assert.Contains("viewbox=",handler.Requests[^1]);
        Assert.Equal(3,handler.Requests.Count);
    }

    [Fact]
    public async Task FailedSearchIsNotReturnedAsAnEmptyResultOrCached()
    {
        var handler = new ScriptedHandler((_,_) => Reply("Unavailable",HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = WithFallback();settings["Maps:EnableGeocodingCampsiteFallback"] = "false";
        var maps = new MapService(http,settings,cache);
        await Assert.ThrowsAsync<CampsiteSearchException>(() => maps.Campsites(52,5,20,CancellationToken.None));
        await Assert.ThrowsAsync<CampsiteSearchException>(() => maps.Campsites(52,5,20,CancellationToken.None));
        Assert.Equal(4,handler.Requests.Count);
    }

    [Fact]
    public async Task RoutingReturnsEveryLegInOrderAndKeepsMidpointsOnRoadGeometry()
    {
        var handler = new FixtureHandler("""
        {"code":"Ok","routes":[{"distance":300000,"duration":10800,"geometry":{"coordinates":[[5,52],[6,53],[7,52],[8,51]]},"legs":[
          {"distance":200000,"duration":7200,"steps":[{"geometry":{"coordinates":[[5,52],[6,53],[7,52]]}}]},
          {"distance":100000,"duration":3600,"steps":[{"geometry":{"coordinates":[[7,52],[8,51]]}}]}
        ]}]}
        """);
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var route = await new MapService(http,Settings(),cache).Route([new(52,5),new(52,7),new(51,8)],CancellationToken.None);
        Assert.NotNull(route);
        Assert.Equal(new double[] {200,100},route.Legs.Select(l => l.DistanceKm));
        Assert.Equal(new double[] {2,1},route.Legs.Select(l => l.DurationHours));
        Assert.Equal(53,route.Legs[0].Midpoint.Latitude);
        Assert.Equal(6,route.Legs[0].Midpoint.Longitude);
        Assert.Equal(route.DistanceKm,route.Legs.Sum(l => l.DistanceKm));
        Assert.Contains("steps=true",handler.LastUri);
    }

    [Fact]
    public async Task HalfwayStopInterpolatesOnAStraightRoadInsteadOfRepeatingEndpoint()
    {
        var handler = new FixtureHandler("""{"code":"Ok","routes":[{"distance":222000,"duration":7200,"geometry":{"coordinates":[[0,0],[2,0]]},"legs":[{"distance":222000,"duration":7200,"steps":[{"geometry":{"coordinates":[[0,0],[2,0]]}}]}]}]}""");
        using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        var route = await new MapService(http,Settings(),cache).Route([new(0,0),new(0,2)],CancellationToken.None);
        Assert.Equal(1,route!.Legs[0].Midpoint.Longitude,6);
        Assert.Equal(0,route.Legs[0].Midpoint.Latitude);
    }

    private static HttpResponseMessage Reply(string json,HttpStatusCode status = HttpStatusCode.OK) => new(status) {
        Content = new StringContent(json,System.Text.Encoding.UTF8,"application/json")
    };
    private static IConfiguration WithFallback()
    {
        var settings = Settings();settings["Maps:OverpassFallbackUrls:0"] = "https://backup.example/api";return settings;
    }

    private static IConfiguration Settings() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Maps:NominatimUrl"] = "https://geo.example/",["Maps:OverpassUrl"] = "https://overpass.example/api",["Maps:RoutingUrl"] = "https://route.example/"
    }).Build();
}

internal sealed class PlannerFactory : WebApplicationFactory<Program>
{
    private readonly string directory;
    private readonly bool ownsDirectory;
    public PlannerFactory(string? directory = null)
    {
        ownsDirectory = directory is null;
        this.directory = directory ?? Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Data:Directory",directory);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && ownsDirectory && Directory.Exists(directory)) Directory.Delete(directory,true);
    }
}
internal sealed class FixtureHandler(string json) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string LastBody { get; private set; } = "";
    public string LastUri { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        Calls++; LastUri = request.RequestUri!.AbsoluteUri;
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json,System.Text.Encoding.UTF8,"application/json") };
    }
}

internal sealed class ScriptedHandler(Func<HttpRequestMessage,int,HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(respond(request,Requests.Count));
    }
}
