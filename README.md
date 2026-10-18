# Roam — Roadtrip Planner

A self-hosted roadtrip planner built with **ASP.NET Core / .NET 10**, a responsive JavaScript interface and Leaflet maps. Plan several trips, change the route freely, and discover campings or camper/caravan sites near each stop. No account or paid API key required.

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

- Create as many trips as you need; optionally set a departure date.
- Search a place by name, then add the correct result. You can also enter coordinates manually.
- Set nights per stop (zero means a day visit). Arrival dates shift automatically as you reorder or change nights.
- Drag the handle to reorder, or use the up/down buttons. Add stop notes and a trip notebook.
- Choose **Find campsites** on a stop to search within 10–50 km. Filter campings or camper/caravan sites; save a campsite against that stop.
- Choose **Calculate driving route** for a road route, distance and driving time. Changing stops invalidates the previous route. These are car estimates between your places, not campsite entrances; always verify camper dimensions, road restrictions, ferries and traffic yourself.
- Use **Save changes** (or Ctrl/Cmd+S). The app warns before leaving unsaved work; a version check prevents silently overwriting another tab's edits. On a conflict, export your edits if needed, then reload to get the latest version.
- Trip settings let you rename, change departure date, duplicate, export or delete a trip. Importing an export creates a separate trip.

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

The tests cover trip CRUD, multiple trips, persisted stop order/nights/campsites across application restarts, concurrent edit conflicts, validation, cross-origin writes, geocoding, campsite filtering/cache and routing. Provider tests use fixtures; they do not depend on live public services. GitHub Actions builds and tests every push/PR, builds the Docker image, and smoke-tests HTTP access and persistence through a container restart.

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
| Campsites | [Overpass API](https://overpass-api.de/) / OpenStreetMap `tourism=camp_site` and `tourism=caravan_site` |
| Driving route | [OSRM public demo server](https://router.project-osrm.org/) |
| Map tiles | [OpenStreetMap](https://www.openstreetmap.org/) |
| Map library | Leaflet 1.9.4 bundled locally (license included); system fonts |

Searches send the entered place name to Nominatim, coordinates/radius to Overpass, and route coordinates to OSRM. These are public community services with availability and usage limits. There is no autocomplete: geocoding is submitted explicitly, cached for 24 hours, and serialized with a gap over one second between outbound requests. Campsites/routes are cached in memory for six hours. Restarting clears these caches. Discovery endpoints are additionally limited to 20 requests/minute per application instance. This setup targets a small personal installation, not public hosting or bulk searches.

Campsite listings are not a booking engine: coverage and facilities vary, and no live opening/availability/price check is performed. Private/inaccessible and explicitly disused campsites are excluded when tagged. Distances shown are straight-line distances from the stop. Map data © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), available under ODbL. Review the [Nominatim policy](https://operations.osmfoundation.org/policies/nominatim/) and [tile policy](https://operations.osmfoundation.org/policies/tiles/) when changing deployment scale.

Providers are configurable without rebuilding, for example in Compose `environment`:

```yaml
Maps__NominatimUrl: https://your-geocoder.example/
Maps__OverpassUrl: https://your-overpass.example/api/interpreter
Maps__RoutingUrl: https://your-osrm.example/
Maps__UserAgent: RoadtripPlanner/1.0 (+https://your-contact-page.example)
```

URLs must expose the same Nominatim, Overpass or OSRM-compatible API respectively. The tile URL is in `wwwroot/app.js`. The interface and Leaflet assets are served locally, so saved trip editing works without an internet connection while the server is running.

## Access

Compose binds to `127.0.0.1` by default. This is a trusted local app with **no login**: anyone with access can read/edit/delete trips. For trusted LAN access, change the port binding to `${ROADTRIP_PORT:-8088}:8080`, restrict it with your firewall, and use `http://YOUR-SERVER-IP:8088`. Do not expose it publicly without authentication and HTTPS at a reverse proxy. Cross-origin browser writes are rejected; normal API requests can be made from local scripts. `GET /health` provides a simple liveness check.
