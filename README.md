# BetStats 2.0

Multi-sport data, probabilistic prediction, simulation and AI-assisted operations platform.

> **Status:** Architecture baseline v1.0 / implementation bootstrap.

## Product boundary

BetStats is **not a bookmaker**. It does not accept real-money stakes, deposits or withdrawals and does not execute bets. The Prediction Playground uses virtual coupons for analytics, education and entertainment.

## First vertical slice

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

## Technology baseline

- .NET 10
- ASP.NET Core
- EF Core
- PostgreSQL
- Docker
- Testcontainers
- OpenTelemetry
- GitHub Actions
- Python permitted behind an explicit ML boundary

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
  BetStats.IntegrationTests
  BetStats.ArchitectureTests
  BetStats.ProviderContractTests
  BetStats.DataQualityTests
  BetStats.SecurityTests
  BetStats.AIEvaluationTests
  BetStats.ModelRegressionTests

docs/
  en/
  pl/
  adr/
```

## Architecture rules

Read [`AGENTS.md`](AGENTS.md) before changing the codebase. Architecture-impacting changes require an ADR.

## Data and licensing

Source code licensing and sports-data licensing are separate concerns. No third-party dataset is licensed by this repository. See [`THIRD_PARTY_DATA.md`](THIRD_PARTY_DATA.md).

## License

Copyright © 2026. All rights reserved. This repository is publicly viewable but is not currently released under an OSI-approved open-source license. See [`LICENSE`](LICENSE).
