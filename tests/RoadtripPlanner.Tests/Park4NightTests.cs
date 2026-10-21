using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using RoadtripPlanner;
using Xunit;

namespace RoadtripPlanner.Tests;

public sealed class Park4NightTests
{
    [Fact]
    public async Task GuestEnvelopeParsesStringsFacilitiesRatingsAndDistancesAndCachesByCentre()
    {
        var handler = new ScriptedHandler((_,_) => Reply("""
        {"status":"OK","lieux":[{"id":"123","latitude":"52.301","longitude":"5.62","name":"Sun camp","code":"C","publique":1,
        "distance":"999","note_moyenne":"4.50","nb_commentaires":"12","point_eau":"1","douche":1,"electricite":true,"wifi":"0",
        "site_internet":"https://example.com","tel":"+311234567"}]}
        """));
        using var client = new HttpClient(handler);using var cache = Cache();
        var service = new Park4NightService(client,Settings(),cache);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var camp = Assert.Single(await service.Search(52.3,5.62,10,CancellationToken.None));
            Assert.Equal("park4night/123",camp.Id);Assert.Equal("Park4Night",camp.Source);
            Assert.Equal("https://park4night.com/en/place/123",camp.SourceUrl);
            Assert.Equal(4.5,camp.Rating);Assert.Equal(12,camp.ReviewCount);
            Assert.InRange(camp.DistanceKm,0,.2);Assert.Equal("camp_site",camp.Type);
            Assert.Equal(new[] {"Water","Showers","Electricity"},camp.Facilities);
            await service.Search(52.3,5.62,50,CancellationToken.None);
            Assert.Single(handler.Requests);
            Assert.Contains("latitude=52.3&longitude=5.62",handler.Requests[0]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task KnownOvernightCategoriesStaySeparateFromDayOnlyPicnicAndServicePoints()
    {
        var codes = new[] {"C","ACC_G","ACC_P","ACC_PR","PSS","F","P","PN","PJ","DS","APN","AR","UNKNOWN"};
        var rows = codes.Select((code,i) => new { id=i+1,latitude=52.3,longitude=5.62,titre=$"Place {code}",code });
        var json = System.Text.Json.JsonSerializer.Serialize(rows);
        var handler = new ScriptedHandler((_,_) => Reply(json));
        using var client = new HttpClient(handler);using var cache = Cache();
        var camps = await new Park4NightService(client,Settings(),cache).Search(52.3,5.62,20,CancellationToken.None);
        Assert.Equal(8,camps.Length);
        Assert.Single(camps,c => c.Type=="camp_site");Assert.Equal(5,camps.Count(c => c.Type=="caravan_site"));
        Assert.Single(camps,c => c.Type=="overnight_parking");Assert.Single(camps,c => c.Type=="nature_spot");
        Assert.DoesNotContain(camps,c => c.Name.Contains("PJ") || c.Name.Contains("APN") || c.Name.Contains("DS"));
    }

    [Fact]
    public async Task InvalidRowsUnpublishedPlacesAndSitesBeyondExactRadiusAreExcluded()
    {
        var handler = new ScriptedHandler((_,_) => Reply("""
        [{"id":1,"latitude":52.3,"longitude":5.62,"code":"C","name":"Valid","note_moyenne":"99","nb_commentaires":0,"site_internet":"javascript:alert(1)"},
        {"id":2,"latitude":52.309,"longitude":5.62,"code":"C","name":"Just outside 1 km"},
        {"id":3,"latitude":"NaN","longitude":5.62,"code":"C"},
        {"id":4,"latitude":91,"longitude":5.62,"code":"C"},
        {"id":"../bad","latitude":52.3,"longitude":5.62,"code":"C"},
        {"id":5,"latitude":52.3,"longitude":5.62,"code":"C","publique":0},
        {"id":1,"latitude":52.3,"longitude":5.62,"code":"C","name":"Duplicate"}]
        """));
        using var client = new HttpClient(handler);using var cache = Cache();
        var camp = Assert.Single(await new Park4NightService(client,Settings(),cache).Search(52.3,5.62,1,CancellationToken.None));
        Assert.Equal("Valid",camp.Name);Assert.Null(camp.Rating);Assert.Null(camp.Website);
    }

    [Theory]
    [InlineData("{\"status\":\"ERROR\",\"lieux\":[]}")]
    [InlineData("{\"message\":\"Service changed\"}")]
    [InlineData("{\"status\":\"OK\",\"lieux\":{}}")]
    public async Task ProviderErrorsAreNotSilentlyReturnedAsEmptyOrCached(string payload)
    {
        var handler = new ScriptedHandler((_,_) => Reply(payload));
        using var client = new HttpClient(handler);using var cache = Cache();
        var service = new Park4NightService(client,Settings(),cache);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.Search(52.3,5.62,20,CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.Search(52.3,5.62,20,CancellationToken.None));
        Assert.Equal(2,handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneUnavailableProviderDoesNotHideOtherProviderResults(bool parkAvailable)
    {
        using var mapsHttp = new HttpClient(new ScriptedHandler((_,_) => parkAvailable ? Reply("Busy",HttpStatusCode.ServiceUnavailable) : Reply(Osm)));
        using var parkHttp = new HttpClient(new ScriptedHandler((_,_) => parkAvailable ? Reply(Park) : Reply("Busy",HttpStatusCode.ServiceUnavailable)));
        using var cache = Cache();var config = Settings();
        var service = new CampsiteSearchService(new MapService(mapsHttp,config,cache),new Park4NightService(parkHttp,config,cache));
        var result = await service.Search(52.3,5.62,20,CancellationToken.None);
        Assert.Single(result.Campsites);Assert.Single(result.Warnings);
        Assert.Contains(parkAvailable ? "OpenStreetMap" : "Park4Night",result.Warnings[0]);
    }

    [Fact]
    public async Task BothUnavailableProvidersFailAndDisabledPark4NightNeverCallsIt()
    {
        using var mapsHttp = new HttpClient(new ScriptedHandler((_,_) => Reply("Busy",HttpStatusCode.ServiceUnavailable)));
        var handler = new ScriptedHandler((_,_) => Reply("Busy",HttpStatusCode.ServiceUnavailable));
        using var parkHttp = new HttpClient(handler);using var cache = Cache();var config = Settings();
        var service = new CampsiteSearchService(new MapService(mapsHttp,config,cache),new Park4NightService(parkHttp,config,cache));
        await Assert.ThrowsAsync<CampsiteSearchException>(() => service.Search(52.3,5.62,20,CancellationToken.None));
        Assert.Single(handler.Requests);
        config["Park4Night:Enabled"]="false";
        Assert.Empty(await new Park4NightService(parkHttp,config,cache).Search(52.3,5.62,20,CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ConservativeDedupKeepsAttributionRatingsAndFacilitiesWithoutMergingNeighbours()
    {
        var osm = new Campsite("way/1","Sun Camp",52.3,5.62,0,"camp_site",null,null,["Water"]);
        var p4n = new Campsite("park4night/1","Sun Camp",52.3001,5.6201,0,"camp_site","https://example.com",null,["Showers"],Source:"Park4Night",SourceUrl:"https://park4night.com/en/place/1",Rating:4.5,ReviewCount:12);
        var other = p4n with {Id="park4night/2",Name="Another business",SourceUrl="https://park4night.com/en/place/2"};
        var far = p4n with {Id="park4night/3",Latitude=52.31,SourceUrl="https://park4night.com/en/place/3"};
        var merged = CampsiteSearchService.Merge([osm,p4n,other,far]);
        Assert.Equal(3,merged.Length);
        var camp = Assert.Single(merged,c => c.Id=="way/1");
        Assert.Equal(new[] {"Water","Showers"},camp.Facilities);Assert.Equal(4.5,camp.Rating);Assert.Equal(12,camp.ReviewCount);
        Assert.Equal("https://park4night.com/en/place/1",Assert.Single(camp.OtherSources!).Url);
        Assert.Single(CampsiteSearchService.Merge([p4n,p4n]));
    }

    [Fact]
    public async Task CancelledSearchDoesNotTurnIntoPartialSuccessOrContactProviders()
    {
        var osmHandler = new ScriptedHandler((_,_) => Reply(Osm));var parkHandler = new ScriptedHandler((_,_) => Reply(Park));
        using var osmHttp = new HttpClient(osmHandler);using var parkHttp = new HttpClient(parkHandler);using var cache = Cache();
        var config = Settings();var service = new CampsiteSearchService(new MapService(osmHttp,config,cache),new Park4NightService(parkHttp,config,cache));
        using var cancel = new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Search(52.3,5.62,20,cancel.Token));
        Assert.Empty(osmHandler.Requests);Assert.Empty(parkHandler.Requests);
    }

    [Fact]
    public async Task Park4NightChoiceAndMetadataSurviveSaveAndReload()
    {
        using var factory = new PlannerFactory();using var client = factory.CreateClient();
        var selected = new Campsite("park4night/123","Sun Camp",52.3,5.62,0,"camp_site",null,null,["Water"],Source:"Park4Night",SourceUrl:"https://park4night.com/en/place/123",Rating:4.5,ReviewCount:12);
        var response = await client.PostAsJsonAsync("/api/trips",new Trip {Name="Stay",Stops=[new Stop {Name="Ermelo",Latitude=52.3,Longitude=5.62,Campsite=selected}]});
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var trip = await response.Content.ReadFromJsonAsync<Trip>();
        var saved = await client.GetFromJsonAsync<Trip>($"/api/trips/{trip!.Id}");
        Assert.Equal("Park4Night",saved!.Stops[0].Campsite!.Source);Assert.Equal(4.5,saved.Stops[0].Campsite!.Rating);
        Assert.Equal(selected.SourceUrl,saved.Stops[0].Campsite!.SourceUrl);
        Assert.Equal("OpenStreetMap",System.Text.Json.JsonSerializer.Deserialize<Campsite>("{\"Id\":\"way/1\",\"Name\":\"Old camp\",\"Latitude\":52,\"Longitude\":5,\"DistanceKm\":0,\"Type\":\"camp_site\",\"Facilities\":[]}")!.Source);
    }

    private const string Park = """{"status":"OK","lieux":[{"id":123,"latitude":52.3,"longitude":5.62,"name":"Park camping","code":"C"}]}""";
    private const string Osm = """{"elements":[{"type":"way","id":1,"center":{"lat":52.3,"lon":5.62},"tags":{"tourism":"camp_site","name":"OSM camping"}}]}""";
    private static MemoryCache Cache() => new(new MemoryCacheOptions());
    private static HttpResponseMessage Reply(string json,HttpStatusCode code = HttpStatusCode.OK) => new(code) {Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
    private static IConfiguration Settings() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Maps:OverpassUrl"]="https://osm.example/api",["Maps:EnableGeocodingCampsiteFallback"]="false",["Park4Night:SearchUrl"]="https://park.example/nearby"
    }).Build();
}
