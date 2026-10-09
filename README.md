# BetStats 2.0

Multi-sport data, probabilistic prediction, simulation and AI-assisted operations platform.

> **Status:** BS-012 bounded historical Elo/Poisson models, optional Dixon-Coles, mathematical simulations and fenced backtests; live provider access disabled.
> PostgreSQL stores ingestion metadata, generic sports entities, versioned policies,
> audited identity decisions and immutable observations with bounded history. API liveness, Web placeholder
> and Worker host remain minimal. Real providers, trained predictions,
> authentication and localization remain planned.

## Local API development

```sh
dotnet run --project src/BetStats.Api --launch-profile http
```

Swagger: http://localhost:5000/swagger; OpenAPI: http://localhost:5000/swagger/v1/swagger.json.
In Visual Studio, set `BetStats.Api` as the startup project, select the `http` profile,
and start debugging (F5). The profile opens Swagger automatically. `BetStats.Web`
is a separate placeholder host and does not serve the API documentation.
Development-only documentation; public reads are liveness and project-owned sport codes.
See [historical integrity, API setup and security boundaries](docs/en/data/historical-integrity-development-api.md)
and [ADR 0022](docs/adr/0022-historical-integrity-and-development-api.md).

## Product boundary

[BS-012 football models and walk-forward runbook](docs/en/data/football-models.md)
documents explicit model parameters, warm-up exclusions and equivalent-event comparisons.
No calibrated ensemble or real-world predictive performance is claimed.
See [ADR 0026](docs/adr/0026-football-prediction-models.md).

[BS-011 historical backtesting and operator runbook](docs/en/data/historical-backtesting.md)
describes synthetic predictions and immutable evaluation reports. Strict historical
evidence gates can produce zero eligible samples; no real model performance is claimed.
See [ADR 0025](docs/adr/0025-historical-backtesting.md).

[BS-010 result coverage, v3 operator commands, recovery and event end](docs/en/data/result-coverage-operations-event-end.md)
documents explicit Development-only operations and additive migrations. Completeness
requires independent fictional evidence; BS-011 consumes its evaluation eligibility
contract. See [ADR 0024](docs/adr/0024-result-coverage-operations-and-event-end.md).

[BS-009 football results, outcome availability, v3 datasets and fictional development API](docs/en/data/football-results-outcomes.md)
describes the explicit results demo and additive migration. Observed statistics remain
partial history; no prediction model is trained or scored. See [ADR 0023](docs/adr/0023-football-results-and-outcome-provenance.md).

[BS-007 dataset snapshots and features](docs/en/data/dataset-snapshots-features.md)
documents explicit synthetic Worker commands, immutable evidence, verification,
comparison and recovery. See [ADR 0020](docs/adr/0020-dataset-snapshots-features.md).
Metadata features describe partial observed history; no model is trained or validated.

[BS-006 operator workflow and historical eligibility](docs/en/data/quality-identity-reconciliation.md)
documents explicit Worker commands, bounded reports, immutable quality/maintenance
audit and HistoricalAsKnown versus RetrospectiveReconstruction. See
[ADR 0019](docs/adr/0019-quality-review-reconciliation.md). Operator identifiers are
manually supplied claims; administrative HTTP endpoints are not exposed.

BetStats is **not a bookmaker**. It does not accept real-money stakes, deposits or withdrawals and does not execute bets. The Prediction Playground uses virtual coupons for analytics, education and entertainment.

## Planned first vertical slice

```text
Football
  ↓
one competition
  ↓
one permitted free provider
  ↓
RAW
  ↓
Observation
  ↓
Canonical
  ↓
FeatureSnapshot
  ↓
PredictionSnapshot
  ↓
Evaluation
```

## Technology baseline and prerequisites

- Stable .NET 10 SDK, minimum 10.0.100. `global.json` permits newer 10.0
  feature bands (`latestFeature`) and excludes prerelease SDKs.
- ASP.NET Core
- PostgreSQL 17, EF Core 10 and Npgsql
- Docker with Linux containers (required for integration tests)
- GitHub Actions
- Python permitted behind an explicit ML boundary

Testcontainers supplies disposable PostgreSQL for integration tests. OpenTelemetry
and the Python ML subsystem are planned. Restore requires access to NuGet.org.

## Restore, build and test

Run from the repository root:

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
```

The solution uses Central Package Management in `Directory.Packages.props`.
Shared `Directory.Build.props` enables nullable reference types, implicit usings,
deterministic builds, SDK analyzers and warnings as errors. Test projects inherit
these settings; package versions belong in the central file, not project files.

## Repository

```text
src/
  BetStats.Api
  BetStats.Worker
  BetStats.Domain
  BetStats.Application
  BetStats.Infrastructure
  BetStats.Web

tests/
  BetStats.UnitTests
  BetStats.ArchitectureTests
  BetStats.IntegrationTests

docs/
  en/
  pl/
  adr/
```

| Project | Responsibility | Allowed project dependencies |
| --- | --- | --- |
| Domain | Canonical entities, source policies, identity and observation invariants | None |
| Application | Fail-closed policy evaluation, provider contracts/budgets and historical query contracts | Domain |
| Infrastructure | PostgreSQL metadata, canonical/provenance mappings, adapters and migrations | Application, Domain |
| Api | ASP.NET Core composition root; `/health/live` | Application, Infrastructure, Domain |
| Worker | Generic Host composition root | Application, Infrastructure, Domain |
| Web | ASP.NET Core presentation placeholder | Application, Domain |

Allowed dependencies are not required references. The existing minimal reference
set is preserved. API and Worker wire infrastructure; Web does not reference
Infrastructure or persistence implementations. See [ADR 0013](docs/adr/0013-project-dependency-direction.md).

`BetStats.UnitTests` covers Domain/Application invariants, conservative availability,
UTC and correction rules, plus configuration precedence for host builders.
`BetStats.ArchitectureTests` evaluates the source projects through MSBuild in
Debug and Release, rejects forbidden edges, cycles, unregistered projects,
binary reference bypasses and direct persistence packages outside Infrastructure.
Run these tests from a source checkout with the .NET SDK installed.
`BetStats.IntegrationTests` verifies fresh/BS-002 upgrade migrations, relational
constraints, append-only history, corrections and negative temporal leakage cases
on real PostgreSQL, plus registration without startup database access. All suites run with
`dotnet test BetStats.slnx`; Docker is required and integration failures are not skipped.

CI runs restore, Release build, unit tests, architecture tests and PostgreSQL
integration tests on pull requests and `main` pushes, on Ubuntu with Docker.
A failing test fails CI. CodeQL keeps the BS-000 manual Release build and uploads
analysis results with `security-events: write`.

## Development workflow and configuration

1. Read `AGENTS.md` and relevant ADRs; start from an Issue and branch from `main`.
2. Keep changes within its acceptance criteria and add meaningful regression tests.
3. Run the three verification commands above and open a PR targeting `main`.
4. Review CI and CodeQL results before a human merges the PR.

Run individual hosts with `dotnet run --project src/BetStats.Api` (or
`src/BetStats.Worker` / `src/BetStats.Web`). Default host configuration loads
optional `appsettings.json`, environment-specific JSON, environment variables,
then command-line arguments. Environment values override JSON; command-line
values override environment values. Use double underscores for nested keys,
for example `Logging__LogLevel__Default=Information`. Set `DOTNET_ENVIRONMENT`
for Worker, or `ASPNETCORE_ENVIRONMENT` for API/Web, to select Development locally.

Do not put production credentials into tracked settings. `.env`, local settings,
certificates, build outputs and local/provider datasets are ignored. Ignored
`appsettings.*.local.json` files are **not automatically loaded** by these hosts.
Use environment variables for sensitive settings. .NET does not automatically
load `.env`; it is used by Docker Compose. The `.env.example` values are public
development placeholders, not production configuration.

## PostgreSQL and migrations

Copy `.env.example` to ignored `.env` and replace the placeholder password.
Compose requires `POSTGRES_PASSWORD`; database/user/port can be configured there.
The port binds to `127.0.0.1` and data persists in a named Docker volume.

```sh
docker compose up -d --wait postgres
docker compose ps
docker compose exec postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
docker compose down
```

Set `ConnectionStrings__BetStats` in the API/Worker/EF process environment with
the matching connection string; `.env` is not read automatically by .NET. In
PowerShell: `$env:ConnectionStrings__BetStats = Read-Host 'Local connection string' -MaskInput`.
Persistence is registered by Infrastructure. No connection or migration occurs
at host startup; requesting a DbContext requires configuration.

```sh
dotnet tool restore
dotnet ef migrations list --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations script --idempotent --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --output artifacts/persistence.sql
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet test tests/BetStats.IntegrationTests --configuration Release --no-build
```

Review migration SQL before applying it. Initial tables are `ingestion.DataSources`
(source configuration), `IngestionRuns` (execution) and `RawPayloads` (immutable
capture metadata and storage reference). They are not canonical sports entities.
Keys are UUIDs, timestamps are UTC `timestamp with time zone`, and FKs use RESTRICT
to protect history. Raw bytes/credentials are not stored in these tables.

Ordinary shutdown preserves data. To **delete all local development data**,
confirm the Compose project, run `docker compose down --volumes`, restart and
reapply migrations. Never reset a shared/production database for tests.
SourcePolicy and permitted purposes must be checked before future ingestion.
Retention must later coordinate licensed payload storage and metadata through an
explicit audited process; no automatic purge is implemented.
See [complete setup, migration and retention instructions](docs/en/data/persistence-foundation.md)
and [ADR 0014](docs/adr/0014-persistence-foundation.md).

BS-003 adds six `canonical` tables and three `provenance` tables, preserving the
initial migration and ingestion rows. Canonical UUIDs are independent of source IDs.
Four reference sports are seeded; all other data in tests is synthetic.
Observation history filters `AvailableAtUtc <= AsOfUtc` and, since BS-004.1,
`RecordedAtUtc <= AsOfUtc`, then orders by availability,
creation time and UUID. Corrections append new rows and later identity decisions
do not remap earlier observations. Current canonical tables are not historical evidence.
See the [data model and ER diagram](docs/en/data/canonical-sports-model.md),
[query examples and limitations](docs/en/data/canonical-sports-model.md#historical-query-contract)
and [ADR 0015](docs/adr/0015-canonical-identity-and-temporal-observations.md).

BS-004 adds the `governance` schema: immutable source-policy versions, independent
purpose permissions and approval/revocation audit. Unknown permissions deny access;
internal approval is not proof of provider licensing rights. Provider contracts and
an authorization executor exist, with no HTTP clients or real ingestion. Shared
in-process budgets enforce minute/day/concurrency limits, timeout and Retry-After;
they do not coordinate multiple instances. No automatic retries are made.

Identity history now requires database-generated `RecordedAtUtc` as well as decision
event time. Old BS-003 decisions receive conservative migration-time availability,
so pre-migration cutoffs exclude them. Observation history supports keyset pagination
with a default cap of 200 (`History__MaximumPageSize`, 1–1000). The existing list
query throws on overflow; callers should use `ReadPageAsOfAsync` for larger results.
See [governance, provider contracts and limitations](docs/en/data/source-governance.md)
and [ADR 0016](docs/adr/0016-source-governance-and-bounded-history.md).

BS-004.1 adds database-controlled observation recording time, current source gates,
malformed adapter response validation and database RAW immutability. See
[audit remediation, migration and privileged retention](docs/en/data/audit-remediation.md)
and [ADR 0017](docs/adr/0017-audit-remediation-and-trusted-observations.md).
Recording is INSERT time, not a commit-time snapshot guarantee.

BS-005 adds a bounded CSV fixture adapter, exact-byte filesystem RAW storage,
database receipt time, structured ingestion audit, reviewed identity handling,
EventDate observations and batch/row publication receipts. Football-Data.co.uk is
an assessed candidate with restrictive published use conditions, not an approved
live source. No HTTP transport, provider download or startup ingestion is added.
See [synthetic demo, recovery and known limits](docs/en/data/first-football-ingestion.md),
[candidate assessment](docs/en/data/football-data-assessment.md) and
[ADR 0018](docs/adr/0018-first-football-ingestion.md).
After explicitly applying migrations to a dedicated local database:

```sh
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
```

The explicit approval concerns fictional fixtures only. RAW storage must be outside
the repository; filesystem/PostgreSQL writes are not a cross-system transaction.

## Documentation

BS-008 adds explicit reviewed coverage, event-time provenance, v2 dataset manifests
and future evaluation contracts. Existing v1 snapshots remain immutable/readable.
No real-world completeness or model performance is claimed. See
[coverage semantics, development commands and migration](docs/en/data/coverage-time-evaluation.md)
and [ADR 0021](docs/adr/0021-coverage-time-evaluation.md).

- 🇬🇧 [Software & Product Engineering Specification v1.0 — English](docs/specifications/BetStats_2_0_Software_Product_Engineering_Specification_v1_0_EN.docx)
- 🇵🇱 [Specyfikacja Produktu i Inżynierii v1.0 — Polski](docs/specifications/BetStats_2_0_Specyfikacja_Produktu_i_Inzynierii_v1_0_PL.docx)
- [Architecture Decision Records](docs/adr/)
- [Engineering documentation — English](docs/en/)
- [Dokumentacja techniczna — Polski](docs/pl/)

The Markdown documentation is the living engineering documentation. The DOCX specifications are versioned baseline documents.

## Architecture rules

Read [`AGENTS.md`](AGENTS.md) before changing the codebase. Architecture-impacting changes require an ADR.

## Data and licensing

Source code licensing and sports-data licensing are separate concerns. No third-party dataset is licensed by this repository. See [`THIRD_PARTY_DATA.md`](THIRD_PARTY_DATA.md).

## License

Copyright © 2026. All rights reserved. This repository is publicly viewable but is not currently released under an OSI-approved open-source license. See [`LICENSE`](LICENSE).
