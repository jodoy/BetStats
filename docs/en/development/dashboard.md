# Football dashboard (BS-014)

The Web host is a server-interactive Blazor application. It references Application, calls the API over HTTP and never connects to PostgreSQL. Both dashboard hosts allow Development loopback requests only. Do not publish them through a reverse proxy; an authenticated remote deployment is a separate change.

## Local presentation demo

From the repository root, with .NET 10 installed:

```powershell
dotnet restore BetStats.slnx
dotnet build BetStats.slnx -c Release --no-restore
dotnet run --project src/BetStats.Api -c Release --no-build --no-launch-profile -- --environment Development --urls http://localhost:5080 --Dashboard:DemoEnabled true
```

In a second terminal:

```powershell
dotnet run --project src/BetStats.Web -c Release --no-build --no-launch-profile -- --environment Development --urls http://localhost:5081 --Dashboard:ApiBaseUrl http://localhost:5080/
```

Open `http://localhost:5081/`. DEMO is a fictional in-memory presentation dataset, requires explicit API configuration and never accesses or seeds PostgreSQL. All views retain the DEMO banner, including empty model/backtest views. No observed accuracy is supplied. To stop, press Ctrl+C in each terminal. The user's Visual Studio launch profile is independent of these commands.

## Existing governed data

Start PostgreSQL using the existing persistence setup and apply migrations explicitly using the documented operator workflow. Configure `ConnectionStrings__BetStats` and `Ingestion__RawStoragePath` in the API terminal; keep credentials out of source control and command history. Run the API command above without `--Dashboard:DemoEnabled true`. Web needs only `Dashboard:ApiBaseUrl`, defaulting to `http://localhost:5080/`.

An empty database produces an empty dashboard. Use the existing explicit import, identity review, dataset and backtest workflows to publish evidence before reading it. Neither host performs startup migrations, imports, training, prediction, evaluation or scheduling.

Dashboard source rights require currently approved PublicDisplay, InternalAnalytics, HistoricalRetention and RawPayloadStorage permissions, an enabled source, retained RAW with verified hashes, and valid existing artifact inspections. Changed/revoked permissions and expired retention fail closed. Attribution requirements currently fail closed because the MVP does not render arbitrary licensing text. A display grant must be explicitly reviewed; an analytics grant is insufficient.

## Read API

All resources are GET-only beneath `/api/v1/dashboard/`: `competitions`, `seasons`, `teams`, `fixtures`, `predictions`, `models`, `backtests`, `quality`, `provenance`. They require Development, a loopback connection, a loopback Host and an absent or loopback Origin; forwarded headers do not grant access. Responses use `Cache-Control: no-store`. Swagger remains Development-only.

Optional query parameters: `Competition`, `Season`, `Team` (UUIDs), `From`, `To` (`yyyy-MM-dd`), `Status`, `Offset` (0–10000), `Limit` (1–100, default 20). Catalogs sort by canonical UUID; fixtures by date then event UUID, selecting the latest stored knowledge cutoff per event; reports sort by recorded time descending then artifact UUID. SQL selects bounded page keys before loading artifact bytes. Metadata status filters refer to frozen snapshot status. Current eligible regulation scores are separately checked and may reveal completion or conflict after that snapshot's cutoff. No kickoff time is inferred from a date.

Predictions/model definitions exist only inside verified finalized backtest artifacts. Models/predictions therefore use report pagination; their DTOs contain selected frozen predictions and safe model forecasts, never executable requests. Date/team/status filters select reports and prediction rows; metrics always describe the original entire stored report, not a newly evaluated filtered cohort. No evaluation runs on GET. The common eligible sample count is shown only for reports with equal dataset, label evidence, evaluation cutoffs and metric contracts. No ranking or recomputed comparable metrics is claimed.

Quality summarizes the first 100 filtered fixtures and explicitly states that snapshot evidence does not certify source-wide inventory completeness, identity/correction coverage or training readiness. Missing historical evidence and unavailable metrics remain unavailable. Stored integrity verification checks canonical artifact hashes, current policy, RAW hashes and frozen forecast bindings; it is distinct from independent model reproducibility verification.

DTOs exclude RAW bytes, storage keys, policy terms/evidence locations, provider credentials and internal frozen records. Immutable schemas and migrations remain unchanged. See ADR 0028 and the BS-014 verification report.
