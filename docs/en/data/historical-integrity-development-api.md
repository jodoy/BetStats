# Historical integrity remediation and API development

BS-008.1 repairs six audit findings. See [ADR 0022](../../adr/0022-historical-integrity-and-development-api.md).

## Historical guarantees

1. RAW capture records immutable `ingestion.FootballRawContexts` before parsing.
   Dataset and coverage interpretation use that original competition/season, then
   historical identity decisions at T or explicit reconstruction R. Incompatible
   requested references or mappings are rejected. Legacy complete batch receipts
   can independently prove a scope by its committed key; unproven legacy scope
   fails closed. No legacy context or recording timestamp is invented.
2. Coverage inventories compare facts only inside the exact half-open intersection.
   Positions and IDs are frozen in new reports. Classification and conflict intervals
   share one rule; different extents and empty subintervals can agree. Strong claims
   still require the independently checked owned fictional inventory contract.
3. Lookback days use an explicit anchor: `CoverageQuery.AsOfUtc` is the prediction
   boundary. For UTC-calendar day D and N days, require exactly [D-N, D).
   Date-only observations do not imply timestamps. Non-lookback partial observed
   ranges may end earlier but never after the boundary. Expiry, conflict and
   permission denial remain disqualifying; window checks never upgrade them.
4. Evaluation eligibility response contract v2 requires explicit target event
   evidence and minimum lead-time horizon (`minimum-lead-time-v2`). Precise kickoff
   uses justified timezone/offset/UTC evidence; calendar policy uses explicitly
   declared UTC-calendar midnight. Missing binding, late receipt, DateOnly kickoff
   and insufficient horizon are ineligible. The caller must supply independently
   validated evidence IDs/binding; no evaluator executor or metrics are implemented.
5. Precise-time RAW contract v1 is `EventTimeSourceClaim`: original ingestion RAW
   UUID, provider event reference, original competition/season context and time
   value. Source, original row and reviewed historical event identity must agree.
   DateOnly retains original RAW/date checks. Corrections preserve earlier cutoffs.
   Legacy bare clock claims remain stored and are returned with an explicit
   `unverified_operator_time_assertion` reason and no justified UTC instant.
6. Dataset verification response contract v2 separates the guarantees below.

| Field | Meaning |
| --- | --- |
| ArtifactIntegrity | Stored artifact bytes, definition fingerprint and hashes agree |
| FrozenMetadataComplete | Frozen database evidence and independently established scope can be verified under current rights |
| CurrentUseAuthorized | Current source enablement, purpose permissions and retention allow use |
| FeaturesReproducible | Frozen feature inputs reproduce stored vectors |
| RawAvailable | Deep inspection found all original RAW; null means not inspected/undetermined |
| RawHashVerified | Deep inspection verified length and SHA-256; null means not inspected |

Standard `verify` performs no RAW reads and leaves both RAW fields null. Explicit
`verify-deep` requires current purposes, RAW storage rights and retention before
reading. Missing/corrupt/unauthorized outcomes have structured reason codes.
Missing original RAW does not invalidate otherwise intact artifact bytes or frozen
feature reproduction. Deprecated `EvidenceComplete` and `CurrentlyAuthorized`
remain aliases for compatibility; they are not RAW verification claims.

Existing finalized v1/v2 artifact bytes and hashes are never rewritten. New v2
manifests use governance/coverage schema 2; schema 1 remains readable and frozen
calculation remains supported. Optional fact fields are omitted when absent.
The additive `20261008150019_HistoricalIntegrityContext` migration creates only
the context table and its trusted INSERT/append-only triggers. Published migrations
are unchanged. Runtime roles must be non-owner as described in ADR 0017.

## Local API and Swagger

Requires stable .NET 10 SDK (minimum 10.0.100, `global.json` roll-forward), with
packages managed in `Directory.Packages.props`. One Microsoft ASP.NET Core OpenAPI
generator supplies Swagger UI; the UI package does not introduce another generator.

```powershell
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
dotnet run --project src/BetStats.Api --launch-profile http
```

The committed `http` profile explicitly selects Development and
`http://localhost:5000`. Open:

- UI: `http://localhost:5000/swagger`
- OpenAPI JSON: `http://localhost:5000/swagger/v1/swagger.json`

Documentation is Development-only. Production returns 404 for these routes and
retains HTTPS redirection. No global CORS, administrative HTTP mutations, RAW
bodies, private provider metadata or audit records are exposed. Liveness does not
mean PostgreSQL readiness. OpenAPI metadata, summaries, schemas, optional query
parameters and response codes are provided. Errors use ProblemDetails without
database exception details; invalid integer parameters return 400 in Development too.

| Route | Public response |
| --- | --- |
| GET /health/live | Existing process liveness response preserved |
| GET /api/v1/health | API process liveness |
| GET /api/v1/sports?offset=0&limit=20 | Four project-owned reference sport codes, ordered by ordinal code |

Pagination: offset 0..10000, limit 1..100. Offset beyond the four reference records
returns an empty Items list. These reads work without a database configuration and
have the same behavior with an empty database. They do not query provider-derived
canonical tables.

```powershell
Invoke-RestMethod http://localhost:5000/api/v1/health
Invoke-RestMethod 'http://localhost:5000/api/v1/sports?offset=0&limit=2'
```

Competitions, seasons, events, datasets, verification and coverage HTTP routes are
intentionally omitted. Existing operator queries require purpose/context and may
return evidence or audit metadata; they are not ready for anonymous PublicDisplay.
A canonical row alone confers no display rights. Future read surfaces must validate
all contributing sources' PublicDisplay and retention, project safe DTOs, and bound
queries before exposure. Authentication is outside this milestone.

## PostgreSQL, migration and synthetic initialization

PostgreSQL 17 and Docker Linux containers are required for persistence/integration
tests. The safe public reads and Swagger do not require PostgreSQL. For local
operator work, follow [persistence setup](persistence-foundation.md): copy
`.env.example` to ignored `.env`, supply your own local password, and run
`docker compose up -d --wait postgres`. .NET does not automatically read `.env`.

```powershell
$env:ConnectionStrings__BetStats = Read-Host 'Local PostgreSQL connection string' -MaskInput
dotnet tool restore
dotnet ef database update --project src/BetStats.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

No migrations or synthetic ingestion run at API startup. `Ingestion__RawStoragePath`
may select an absolute RAW directory outside the repository; otherwise the local
application data BetStats/raw directory is used. Never commit credentials or RAW.
An empty migrated database has only project-owned reference seeds, not approved
provider policies. Synthetic fixtures are opt-in operator commands:

```powershell
dotnet run --project src/BetStats.Worker -- --synthetic-demo --approve-synthetic
```

See [dataset operator commands](dataset-snapshots-features.md) for synthetic dataset
initialization and explicit actor/reason. Substitute `Dataset:Action=verify-deep`
for `verify` to request original RAW inspection. This does not grant provider rights
or expose the synthetic fixtures through anonymous HTTP routes.

## Validation and next step

Maintained unit/PostgreSQL tests replace all eight audit probes with corrected
assertions, and cover interval semantics, temporal horizons, bound time claims,
RAW verification, immutability and artifact compatibility. WebApplicationFactory
tests validate Development/Production documentation, route inventory, schemas,
pagination, ProblemDetails and omission of unauthorized/operator surfaces.

BS-009 should establish one permitted provider's event-time, completeness and
outcome provenance contract before introducing models or evaluation execution.
