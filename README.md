# BetStats 2.0

Multi-sport data, probabilistic prediction, simulation and AI-assisted operations platform.

> **Status:** BS-001 engineering foundation. API liveness endpoint, Web placeholder,
> and Worker host are present. Sports, persistence, prediction, authentication,
> and localization features are not implemented yet.

## Product boundary

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
- PostgreSQL and Docker (optional local development environment)
- GitHub Actions
- Python permitted behind an explicit ML boundary

EF Core, Testcontainers, OpenTelemetry and the Python ML subsystem are planned;
they are not installed or integrated yet. Restore requires access to NuGet.org.

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

docs/
  en/
  pl/
  adr/
```

| Project | Responsibility | Allowed project dependencies |
| --- | --- | --- |
| Domain | Future domain rules; currently empty | None |
| Application | Future use cases and ports; currently empty | Domain |
| Infrastructure | Future adapters and persistence; currently empty | Application, Domain |
| Api | ASP.NET Core composition root; `/health/live` | Application, Infrastructure, Domain |
| Worker | Generic Host composition root | Application, Infrastructure, Domain |
| Web | ASP.NET Core presentation placeholder | Application, Domain |

Allowed dependencies are not required references. The existing minimal reference
set is preserved. API and Worker wire infrastructure; Web does not reference
Infrastructure or persistence implementations. See [ADR 0013](docs/adr/0013-project-dependency-direction.md).

`BetStats.UnitTests` references Domain and Application for future unit tests.
Current tests exercise configuration precedence for the Generic Host and web
builders without running servers. No business logic exists in those layers yet.
`BetStats.ArchitectureTests` evaluates the source projects through MSBuild in
Debug and Release, rejects forbidden edges, cycles, unregistered projects,
binary reference bypasses and direct persistence packages in inner layers/Web.
Run these tests from a source checkout with the .NET SDK installed.

CI runs restore, Release build and all tests on pull requests and `main` pushes.
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

Optional local PostgreSQL: copy `.env.example` to ignored `.env`, replace the
placeholder password and run `docker compose up -d`. Its port is bound to
`127.0.0.1` only. The application does not yet connect to the database; the
example connection-string variable is reserved for future integration. Docker
is not required to run the current tests. This is a development environment,
not a production deployment.

## Documentation

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
