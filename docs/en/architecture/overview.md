# Architecture Overview

BetStats is a modular monolith.

## Engineering dependency baseline

Domain has no project dependencies. Application depends on Domain.
Infrastructure depends on Application and Domain. API and Worker are composition
roots; Web may depend on Application and Domain, never Infrastructure or
persistence implementations. The existing references are preserved and the
allowed graph is enforced by MSBuild-based architecture tests in Debug and
Release. See [ADR 0013](../../adr/0013-project-dependency-direction.md).

The pipeline below describes the target architecture. BS-002 adds PostgreSQL
source/run/RAW capture metadata. BS-003 adds generic canonical entities and
immutable provenance history; provider ingestion and prediction remain planned.
See [persistence boundaries and migration ownership](../../adr/0014-persistence-foundation.md)
and [persistence setup](../data/persistence-foundation.md).

## Canonical and historical boundaries (BS-003)

Domain owns Sport, Competition, Season, Participant, SportingEvent and
EventParticipant, explicit identity decisions and typed observation invariants.
Application exposes `IObservationHistory` and `IIdentityResolutionHistory`.
Infrastructure implements temporal filtering, EF mappings and additive migrations.
API/Worker register these ports without connecting or migrating at startup.
Web references no persistence implementation. No new endpoint or background job is added.

Provider IDs belong to source-scoped anchors, not canonical entities. A versioned
decision ledger supports unresolved/ambiguous states with no guessed target.
Canonical rows are current projections; historical consumers use observation
availability filters and frozen targets, never current projection joins.
PostgreSQL constraints and append-only triggers protect history even for bulk SQL.
See [ADR 0015](../../adr/0015-canonical-identity-and-temporal-observations.md) and
the [model, temporal contract and ER diagram](../data/canonical-sports-model.md).

## Pipeline
`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

## Hard invariants
- Provider DTO != domain entity.
- Provider IDs do not live on canonical entities.
- Conflicts remain observable.
- `FeatureSnapshot` and `PredictionSnapshot` are immutable.
- `AsOfUtc` prevents temporal leakage.
- Core prediction is independent from bookmaker odds.
