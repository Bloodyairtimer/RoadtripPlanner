using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using RoadtripPlanner;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.AddSingleton<TripStore>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("maps", client =>
{
    client.Timeout = TimeSpan.FromSeconds(35);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["Maps:UserAgent"]!);
});
builder.Services.AddSingleton(sp => new MapService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("maps"),
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
builder.Services.AddHttpClient("park4night", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["Maps:UserAgent"]!);
});
builder.Services.AddSingleton(sp => new Park4NightService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("park4night"),
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
builder.Services.AddSingleton<CampsiteSearchService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("routing", o => { o.PermitLimit = 60; o.Window = TimeSpan.FromMinutes(1); o.QueueLimit = 0; });
    options.AddFixedWindowLimiter("discovery", o => { o.PermitLimit = 20; o.Window = TimeSpan.FromMinutes(1); o.QueueLimit = 0; });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    if (context.Request.Method is "POST" or "PUT" or "DELETE")
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}")
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    try { await next(context); }
    catch (CampsiteSearchException e)
    {
        app.Logger.LogWarning(e, "Campsite providers failed");
        await Results.Problem("Campsite search could not reach any available provider. Try again shortly or use the map-search links below.", statusCode: 503).ExecuteAsync(context);
    }
    catch (TripConflictException)
    {
        await Results.Problem("This trip changed in another tab. Reload it before saving again.", statusCode: 409).ExecuteAsync(context);
    }
    catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogWarning(e, "Map provider request failed");
        await Results.Problem("The map service is unavailable or busy. Your saved trips are safe. Try again shortly.", statusCode: 503).ExecuteAsync(context);
    }
    catch (Exception e) when (e is IOException or JsonException)
    {
        app.Logger.LogError(e, "Unable to read or write trip data");
        await Results.Problem("Unable to read or save data. Check the data directory and application logs.", statusCode: 500).ExecuteAsync(context);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/trips", async (TripStore store, CancellationToken ct) => Results.Ok(await store.List(ct)));
app.MapGet("/api/trips/{id:guid}", async (Guid id, TripStore store, CancellationToken ct) =>
    await store.Get(id, ct) is { } trip ? Results.Ok(trip) : Results.NotFound());
app.MapPost("/api/trips", async (Trip input, TripStore store, CancellationToken ct) =>
{
    if (TripValidation.Validate(input) is { } error) return Results.Problem(error, statusCode: 400);
    var trip = await store.Create(input, ct);
    return Results.Created($"/api/trips/{trip.Id}", trip);
});
app.MapPut("/api/trips/{id:guid}", async (Guid id, Trip input, TripStore store, CancellationToken ct) =>
{
    if (TripValidation.Validate(input) is { } error) return Results.Problem(error, statusCode: 400);
    return await store.Update(id, input, ct) is { } trip ? Results.Ok(trip) : Results.NotFound();
});
app.MapDelete("/api/trips/{id:guid}", async (Guid id, int version, TripStore store, CancellationToken ct) =>
    await store.Delete(id, version, ct) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/places", async (string q, MapService maps, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length is < 2 or > 200) return Results.Problem("Enter a place with 2–200 characters.", statusCode: 400);
    return Results.Ok(await maps.Search(q.Trim(), ct));
}).RequireRateLimiting("discovery");
app.MapGet("/api/campsites", async (double latitude, double longitude, int radiusKm, CampsiteSearchService search, CancellationToken ct) =>
{
    if (!TripValidation.ValidCoordinates(latitude, longitude) || radiusKm is < 1 or > 50) return Results.Problem("Use valid coordinates and a radius between 1 and 50 km.", statusCode: 400);
    return Results.Ok((await search.Search(latitude, longitude, radiusKm, ct)).Campsites);
}).RequireRateLimiting("discovery");
app.MapGet("/api/campsites/search", async (double latitude, double longitude, int radiusKm, CampsiteSearchService search, CancellationToken ct) =>
{
    if (!TripValidation.ValidCoordinates(latitude, longitude) || radiusKm is < 1 or > 50) return Results.Problem("Use valid coordinates and a radius between 1 and 50 km.", statusCode: 400);
    return Results.Ok(await search.Search(latitude,longitude,radiusKm,ct));
}).RequireRateLimiting("discovery");
app.MapPost("/api/route", async (RouteRequest input, MapService maps, CancellationToken ct) =>
{
    if (input.Points is null || input.Points.Count is < 2 or > 102 || input.Points.Any(p => p is null || !TripValidation.ValidCoordinates(p.Latitude, p.Longitude)))
        return Results.Problem("Provide 2–102 valid route points.", statusCode: 400);
    return await maps.Route(input.Points, ct) is { } route ? Results.Ok(route) : Results.Problem("No driving route found for these stops.", statusCode: 422);
}).RequireRateLimiting("routing");
app.Run();

public partial class Program { }
