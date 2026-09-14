# Smart-X — PROG7312 POE Part 1

A simulated South African IoT gateway for hydroponic soil moisture, utility wattage and valve states. Includes an ASP.NET Core .NET 10 Minimal API and a responsive HTML/CSS/JavaScript client. The client uses fetch to push and retrieve typed JSON through the API; it is hosted by the API so there is one startup command and no Node dependency.

## Prerequisites

- .NET **10 SDK**, not only the runtime. Check `dotnet --version` starts with `10.`.
- A current browser; Git for source control.
- Optional: Docker Desktop with Linux containers and Compose.
- Optional: Python 3 to run HTTP integration checks.

Download the SDK from https://dotnet.microsoft.com/download/dotnet/10.0.

## Restore, compile and start

Extract the source ZIP. Open PowerShell or a terminal **inside the SmartX directory containing this README**.

```powershell
dotnet restore src/SmartX.Api/SmartX.Api.csproj
dotnet build src/SmartX.Api/SmartX.Api.csproj -c Release
dotnet run --project src/SmartX.Api -c Release --no-build --urls http://localhost:5080
```

Keep the terminal open. Open **http://localhost:5080** in your browser, then select **Sensor Data Ingestion and Telemetry**. API health is **http://localhost:5080/api/health**. Press Ctrl+C in the terminal to stop.

There is no separate frontend server: `wwwroot/index.html`, `styles.css`, and `app.js` are the client application. No npm install, external CDN, API key, physical sensor, database server or HTTPS development certificate is required. The frontend source is checked by JavaScript parsing and browser execution, rather than a transpilation step.

If port 5080 is busy, use `--urls http://localhost:5081` and open that address. Relative API URLs continue working. Do not double-click index.html: serve it through the application.

## Demonstrate the functionality

1. The landing page exposes three pillars. Part 2 and final POE buttons are disabled.
2. Initially 1,000 ESP32-style profiles contain 120 readings each: **120,000 mock packets** across floats, integers and booleans. Startup seeding uses typed jagged batches. The simulator starts stopped so demonstrations are deliberate.
3. Click **Start live simulator**. Devices send at their configured interval. The browser polls every two seconds; there is no WebSocket/MQTT broker in Part 1.
4. Filter the registry; click a device. View its chart, unit, status, last reception age and raw packets.
5. Register an identifier, category and location. `Facility A / Zone 1 / Sub-Zone B` passes recursive validation; `Facility A / Maintenance` is deliberately disabled and rejected.
6. On an Environmental or Power Consumption sensor, click **Inject spike**. The active alert and highlighted point show the excursion. The next normal reading can still trigger a delta alert when returning from the spike; two normal samples establish recovery.
7. Click **Pause sensor** while the simulator is running. After more than three expected intervals, it becomes disconnected. For the default five-second interval this is over 15 seconds, plus at most one dashboard refresh interval. Resume it and wait for a fresh sample. Pausing affects simulated automatic samples, not explicit API/manual sends.
8. Expand the custom reading form. Environmental accepts float percent, Power Consumption accepts integer watts, Actuator accepts `true` or `false`. The API rejects category mismatches, wrong JSON types, missing values, stale/future/out-of-order timestamps and physically invalid numeric ranges.
9. Upload a JSON/TXT/LOG/CSV configuration or log, or PNG/JPG photo. Files are linked to the selected sensor and downloadable. Each file is streamed to disk, with a 5 MiB limit and 10 attachments per sensor.
10. Run **100 rounds** of the load exercise: 100,000 additional packets with the original 1,000 devices. The displayed throughput is **in-process validation/storage**, not an end-to-end network capacity claim.
11. Aggregate two smart meters. The result uses `MeterReading.operator +`; alert deltas use `operator -`. Timestamps are disclosed because this is a latest-value sum, not synchronized energy accounting.

## Required C# concepts

| Requirement | Implementation | Demonstration |
|---|---|---|
| Generics | `Domain.cs`: `TelemetryPacket<T>`, `History<T>`; `Gateway.cs`: separate float/int/bool dictionaries | Three typed POST/GET routes preserve value types without storing payloads as `object` or `dynamic` |
| Operator overloading | `MeterReading` immutable value struct implements `+` and `-` with checked arithmetic | Meter aggregate form; wattage delta alert |
| Advanced arrays and lists | `MakeBatches<T>` builds variable-length `TelemetryPacket<T>[][]`; `History<T>.Import` appends sequentially into `List<T>` | Every sensor imports 120 samples as batches of 64 and 56 |
| Recursion | `DeploymentValidator.Validate` walks a path through the configured deployment tree | Three-level valid path; unknown leaf and disabled branch rejected |
| API integration | `Program.cs` typed Minimal API routes; `wwwroot/app.js` fetch calls | Register, POST packet, GET history, upload file |
| Engagement | Active alert queue, threshold/delta chart markers, freshness labels, contextual guidance and fault simulation | Inject spike, pause sensor and verify recovery |

The generic payload path avoids boxing the value through an `object` container. JSON parsing, packet objects and HTTP handling still allocate: this is not a zero-allocation claim. `object` return types are used for presentation DTOs only, not the raw typed history store.

## API examples

Get a profile ID from `GET /api/sensors?category=Environmental&take=1`. In Postman choose **POST**, Body → raw → JSON, and send to `http://localhost:5080/api/telemetry/environmental`:

```json
{
  "sensorId": "REPLACE-WITH-REGISTERED-ENVIRONMENTAL-GUID",
  "timestamp": "REPLACE-WITH-CURRENT-UTC-ISO-TIMESTAMP",
  "value": 51.5
}
```

For Postman you can use `"timestamp": "{{$isoTimestamp}}"`. Power packets go to `/api/telemetry/power` with an integer; actuator packets go to `/api/telemetry/actuator` with a JSON boolean, not a quoted string. A successful ingestion returns **202**. Inspect values through `/api/telemetry/environmental/{id}?count=120` and equivalent power/actuator routes. See `docs/API.md` for all routes and registration examples.

## Persistence and bounds

Sensor registrations, attachment metadata and attachment bytes persist under `bin/Release/net10.0/data` during the command above. Override with `DataDirectory` (PowerShell: `$env:DataDirectory = "C:\SmartXData"`). The app must have write permission. Back up this directory for profiles and files.

Telemetry and active status are deliberately in memory; restarting reseeds 120 samples per saved sensor. Simulation controls reset. Histories retain up to 2,048 samples; the oldest 256 are removed when full. At 5,000 sensors, the configured registration limit, this can still require substantial memory. The single process uses a lock to protect collections; multiple gateway replicas and production backpressure are outside this prototype. Large load exercises temporarily block other gateway operations.

The local demo has no login or device authentication. Do not expose it publicly. Production extensions include authenticated device identities, TLS, upload content inspection, durable telemetry/incident storage, per-device thresholds, rate limits, MQTT bridging and deduplication by device sequence. Generic float endpoints accept JSON numbers and parse to `float`; they cannot infer whether a sender originally used an integer language type.

## Run checks

```powershell
dotnet run --project tests/SmartX.Checks -c Release
python scripts/http_checks.py
```

The first command runs dependency-free C# assertions and a load exercise. The second starts an isolated API on port 5089, verifies HTTP workflows, and cleans its temporary data. Build the API first because the HTTP runner uses `--no-build`. Tests exit nonzero on failure. GitHub Actions builds the project and runs C# checks automatically.

## Docker

From the repository root:

```powershell
docker compose up --build
```

Open http://localhost:5080. Compose exposes the port on the local machine only and persists profile/attachment data in the `smartx-data` volume. Stop with `docker compose down`; the volume remains. The Dockerfile has a .NET 10 SDK build stage and a non-root ASP.NET runtime stage. Docker files are included but container execution has not been verified.
