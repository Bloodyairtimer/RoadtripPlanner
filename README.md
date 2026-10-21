# Roam — Roadtrip Planner

A self-hosted roadtrip planner built with **ASP.NET Core / .NET 10**, a responsive JavaScript interface and Leaflet maps. Plan several trips, change the route freely, and discover campings or camper/caravan sites near each stop. Standard car routes and campsite discovery work without an API key. Optional TomTom camper routing requires your own TomTom API key.

## Run with Docker

Install Docker Engine with Compose (or Docker Desktop), then:

```bash
git clone https://github.com/Bloodyairtimer/RoadtripPlanner.git
cd RoadtripPlanner
docker compose up --build -d
```

If using a source ZIP, extract it and run `docker compose up --build -d` from the folder containing `compose.yaml`.

Open **http://localhost:8088**. Trips persist in the `trips` Docker volume across container restarts, rebuilds and `docker compose down`. `docker compose down -v` deletes the volume and your trips.

The Docker host port defaults to **8088**; the container listens on **8080**. To choose another host port, set `ROADTRIP_PORT` in your environment or create a `.env` file beside `compose.yaml` containing, for example, `ROADTRIP_PORT=8090`. Apply changes with `docker compose up -d`.

Alternatively, build and run the image directly:

```bash
docker build -t roadtrip-planner .
docker run -d --name roadtrip-planner -p 127.0.0.1:8088:8080 -v roadtrip-data:/data roadtrip-planner
```

The runtime uses the non-root `app` user. For bind mounts instead of named volumes, ensure the directory is writable by the image's app user (UID 1654). Run **one instance per data directory**.

## Planning a trip

- Create as many trips as you need; optionally set start and end dates. The overview shows your chosen travel window; with only a start date, it estimates the finish from planned nights.
- Set **Start from** and **Finish at** above the itinerary. **Return to start** makes a round trip; you can clear or change the endpoints independently of your highlights. Existing trips without endpoints keep working.
- Search a place by name, then add the correct result as a highlight. You can also enter coordinates manually.
- Set nights per stop (zero means a day visit). Arrival dates shift automatically as you reorder or change nights.
- Drag the handle to reorder highlights, or use the up/down buttons. Start and finish stay in place. Add stop notes and a trip notebook.
- Choose **Find campsites** on a stop to search within 10–50 km. Filter campings or camper/caravan sites; optionally include other overnight spots. Filter by OpenStreetMap or Park4Night, review source links/ratings, and save a campsite against that stop.
- Click a leg’s distance or its route line to highlight that leg while keeping the complete route in view. All other legs remain fully visible; the map shows its endpoints, kilometres and driving time. Clear the highlight or choose Fit all stops to restore the full route.
- Routing permits turns at stops (`continue_straight=false`), including before the return leg. OSRM optimises driving time, not minimum kilometres; a longer motorway route may be preferred.
- The map draws the final leg to your finish in coral dashes, so it remains visible over an outward route on the same road. Round trips show a shared S/F marker and include the return distance in the total.
- Road routes update automatically after changing endpoints or stops. Each leg shows its driving distance in kilometres and estimated driving time; the overview shows the total. Use **Update route** to retry or refresh. These are driving estimates between your pinned places, not campsite entrances; always verify camper dimensions, road restrictions, ferries and traffic yourself.
- Choose **Add a stop** on any leg (or click its route line). Add a halfway point following the road geometry, search for an intermediate place, or click the map. New travel stops start at zero nights; choose a camping or change nights to make an overnight stay. You can start with major highlights and split long drives later.
- Use **Save changes** (or Ctrl/Cmd+S). The app warns before leaving unsaved work; a version check prevents silently overwriting another tab's edits. On a conflict, export your edits if needed, then reload to get the latest version.
- Trip settings let you rename, change start/end dates, duplicate, export or delete a trip. Importing an export creates a separate trip.

## TomTom camper routing

New trips default to a private camper profile: **7.48 m long, 2.30 m wide, 2.98 m high, 3,500 kg and a 120 km/h personal speed ceiling**. These are editable under **Trip settings → Route settings** and saved per trip, including duplicate/import/export. Existing trips remain standard car trips until camper routing is enabled explicitly.

1. Create a TomTom developer account and a key with Routing API access. Check the current [TomTom pricing and account limits](https://docs.tomtom.com/pricing).
2. Add these settings to a local `.env` file beside `compose.yaml` (or copy `.env.example` if you do not already have one):

```dotenv
TOMTOM_API_KEY=your-own-key
TOMTOM_MONTHLY_REQUEST_LIMIT=1000
```

3. Apply with `docker compose up --build -d`. Open Trip settings to check key availability and local request usage. The key is never returned to the browser or stored in trip JSON. `.env` is git-ignored; keep it private. For direct .NET/image runs use `TomTom__ApiKey` and `TomTom__MonthlyRequestLimit` environment variables instead. Compose supplies these automatically from `.env`.

The dedicated TomTom client sends dimensions, maximum weight, `vehicleMaxSpeed`, `travelMode=car` and `vehicleCommercial=false` for this **private motorhome up to 3,500 kg** profile. This avoids blindly applying a heavy/commercial truck profile. Heavier motorhomes are rejected rather than silently using this profile. Width defaults to the 2,299 mm published in [Adria's 2019 brochure](https://lp.adria.co.uk/pdf/motorhome_brochure_pricelist.pdf), rounded to 2.30 m; confirm mirror width and actual fitted roof equipment yourself.

TomTom's fastest route uses the speed ceiling during route calculation and returns its own driving times. The ceiling does not override lower road/vehicle limits or make slow roads travel at their posted maximum. There is no blanket percentage added to car duration. Routes use `traffic=false`: historic road-speed assumptions remain, live traffic is excluded, and no future departure time is claimed for a multi-day trip. Breaks are excluded. Restriction coverage depends on the provider's map data; the app is a planning aid, not a guarantee of camper access.

Requests preserve all stops in order, include the final return leg, and avoid unpaved roads where possible. Every returned leg carries its own geometry, time, kilometres and road midpoint. Changing dimensions or speed invalidates the route. Camper routes never silently fall back to OSRM if the key is missing, a provider fails or quota runs out. Uncheck camper routing explicitly to use a standard OSRM car route; its label states that camper restrictions are not applied.

**Local request limit:** default 1,000 outgoing attempts per UTC calendar month, configurable from 0 to 20,000 (0 disables outgoing requests). Attempts, including failures/timeouts, are reserved before sending and persisted atomically under `/data/routing-usage/tomtom.json`. Restarting/rebuilding retains the count; a new UTC month resets it. A corrupt ledger fails closed. Do not delete the usage file to bypass the cap. This is a local guard for this single running app/data volume, not an account-wide billing guarantee: other apps using your key and TomTom's actual plan/charging rules are outside its scope.

Identical routes/profiles are cached in memory for six hours; concurrent identical requests share one outbound attempt. Cache hits consume no new request and still work after the local limit is reached. Profile changes create a distinct cache entry. Failed responses are not cached, there are no automatic retries or redirects, and HTTP request logging is disabled for the TomTom client to avoid logging the API key in its URL.

TomTom tests use documented-response fixtures: parameters, return geometry, profile changes, concurrent caching, quota persistence/month rollover, sanitised failures, cancellation, missing configuration and backward compatibility. No live TomTom request was verified without a user-supplied key.

## Local development

Install the .NET 10 SDK. No Node build step or database is needed.

```bash
dotnet restore RoadtripPlanner.slnx
dotnet run --project src/RoadtripPlanner --urls http://localhost:8080
```

Data defaults to `src/RoadtripPlanner/data` when using `dotnet run` with that project. Set `Data__Directory` for another location.

```bash
dotnet build RoadtripPlanner.slnx -c Release
dotnet test RoadtripPlanner.slnx -c Release
```

The 50 backend tests cover trip CRUD, multiple trips, endpoint persistence, persisted stop order/nights/campsites across application restarts, concurrent edit conflicts, optional date persistence and date range validation, cross-origin writes, geocoding, campsite provider failover/cache, Park4Night parsing/category filtering/radius handling, conservative duplicate merging, metadata persistence, per-leg routing and road midpoints. Eleven Node tests cover endpoint order, intermediate stop insertion, route invalidation and duration formatting. Seven Playwright browser flows cover planning, campsite selection, persistence, responsive layout, search errors, source filters, and saved Park4Night selections. Provider tests use fixtures; they do not depend on live public services. GitHub Actions builds and tests every push/PR, runs the browser flows, builds the Docker image, and smoke-tests HTTP access and persistence through a container restart.

```bash
node --test tests/frontend/*.test.mjs
npm ci --prefix tests/browser
tests/browser/node_modules/.bin/playwright install chromium
npm test --prefix tests/browser
```

## Storage and backups

Trip files are JSON, one file per trip, written to temporary files and atomically replaced after flushing to disk. There is no external database. Stop the container before taking a consistent full backup. For example, from this repository:

```bash
docker compose stop
docker compose cp roadtrip-planner:/data ./trip-backup
docker compose start
```

Individual trips can also be exported as JSON in the interface. To restore a full backup, stop the service, copy the files into `/data`, ensure ownership is correct, and start it again. Keep the data directory private: it contains your travel plans and notes.

## Maps and external services

Planning and saved trip editing use local data. Search, campsite discovery, route calculation and map tiles require internet access:

| Feature | Default provider |
| --- | --- |
| Place search | [Nominatim](https://nominatim.openstreetmap.org/) |
| Campsites | OpenStreetMap via [Overpass API](https://overpass-api.de/) / [Private.coffee](https://overpass.private.coffee/) / bounded Nominatim fallback, combined with Park4Night guest nearby search |
| Driving route | [OSRM public demo server](https://router.project-osrm.org/) |
| Map tiles | [OpenStreetMap](https://www.openstreetmap.org/) |
| Map library | Leaflet 1.9.4 bundled locally (license included); system fonts |

Searches send the entered place name to Nominatim, coordinates/radius to Overpass, the search centre to Park4Night, and route coordinates to OSRM. These are third-party services with availability and usage limits. There is no autocomplete: geocoding is submitted explicitly, cached for 24 hours, and serialized with a gap over one second between outbound requests. Campsites/routes are cached in memory for six hours. Restarting clears these caches. Empty Overpass responses are cached for five minutes; the bounded geocoder fallback is cached for 15 minutes. The latter returns a selection of campings rather than every site in the area and shares the geocoder’s one-request-per-second gate. Incomplete or failed provider responses are never presented as a successful empty search. Discovery endpoints are additionally limited to 20 requests/minute per application instance; automatic routing has a separate 60 requests/minute limit. This setup targets a small personal installation, not public hosting or bulk searches.

Campsite listings are not a booking engine: coverage and facilities vary, and no live opening/availability/price check is performed. Sites explicitly tagged inaccessible, disused or abandoned are excluded. Private-access sites are shown with a warning to verify that visitors are accepted. The camping dialog also offers direct map-search links if the community services are unavailable. Distances shown are straight-line distances from the stop. Map data © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), available under ODbL. Review the [Nominatim policy](https://operations.osmfoundation.org/policies/nominatim/) and [tile policy](https://operations.osmfoundation.org/policies/tiles/) when changing deployment scale.

Providers are configurable without rebuilding, for example in Compose `environment`:

```yaml
Maps__NominatimUrl: https://your-geocoder.example/
Maps__OverpassUrl: https://your-overpass.example/api/interpreter
Maps__OverpassFallbackUrls__0: https://your-backup.example/api/interpreter
Maps__EnableGeocodingCampsiteFallback: "true"
Maps__RoutingUrl: https://your-osrm.example/
Maps__UserAgent: RoadtripPlanner/1.0 (+https://your-contact-page.example)
```

URLs must expose the same Nominatim, Overpass or OSRM-compatible API respectively. The tile URL is in `wwwroot/app.js`. The interface and Leaflet assets are served locally, so saved trip editing works without an internet connection while the server is running.

## Access

Compose binds to `127.0.0.1` by default. This is a trusted local app with **no login**: anyone with access can read/edit/delete trips. For trusted LAN access, change the port binding to `${ROADTRIP_PORT:-8088}:8080`, restrict it with your firewall, and use `http://YOUR-SERVER-IP:8088`. Do not expose it publicly without authentication and HTTPS at a reverse proxy. Cross-origin browser writes are rejected; normal API requests can be made from local scripts. `GET /health` provides a simple liveness check.

The travel emblem in `wwwroot/assets/roam-adventure.png` was generated for this project: campervan, surfboards, palm, sea, mountains and sun on a transparent background.

## Park4Night integration

The application calls the guest nearby endpoint documented by [gtoselli/park4night-api](https://github.com/gtoselli/park4night-api) directly from .NET. There is no Python service or account/token configuration. OpenStreetMap and Park4Night are queried together, so one provider failing does not hide the other's results. A warning identifies an unavailable source. `GET /api/campsites/search` returns `{ campsites, warnings }`; the original `/api/campsites` endpoint still returns a combined array for compatibility.

This is an **unofficial, unsupported endpoint**, not an official developer API. Its response carries a notice that the data is not public and asks consumers to stop parsing; it can change or become unavailable. The adapter makes one nearby request per centre, uses the application's own User-Agent, and does not use account impersonation, pagination or grid scraping. Use is subject to the provider's terms/access policy. No review text, photographs or user profile information is imported.

Park4Night results are cached in memory for 15 minutes (five minutes for a valid empty response), with a minimum one-second request gap. Changing only the radius reuses the same centre cache. Coordinates and exact straight-line distances are validated locally. The provider selects its own nearest-result limit (the live guest response currently returns 100 places), so a larger radius does not guarantee every place in that area is returned.

- Campings (`C`), free/paid/private motorhome areas (`ACC_G`, `ACC_P`, `ACC_PR`), motorhome parking without services (`PSS`) and farm stays (`F`) appear under campings/camper sites with specific labels.
- Day/night parking (`P`) and nature spots (`PN`) appear under **Other overnight spots** / **All overnight types**. Their category is not a guarantee of current permission to stay; inspect the source listing.
- Day-only parking, picnic areas, service-only points, rest areas and unknown categories are excluded.
- Nearby same-type listings with matching normalized names are merged conservatively, keeping both source links and combining known facilities. Distinct neighbouring businesses remain separate; some uncertain duplicates can remain.
- Ratings and review counts are shown when supplied by Park4Night. Follow the Park4Night link for current details, restrictions, reviews and availability. Selecting a place retains its source and rating in the saved trip.

Disable or change the provider with Compose environment settings:

```yaml
Park4Night__Enabled: "false"
Park4Night__SearchUrl: https://guest.park4night.com/services/V4.1/lieuxGetFilter.php
```

Set `Park4Night__Enabled` to `true` (the default) to include it. The configured endpoint must expose the same response contract; no authentication workaround is attempted if access is denied.
