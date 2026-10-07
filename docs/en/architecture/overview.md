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
source/run/RAW capture metadata in Infrastructure; no sports pipeline exists.
See [persistence boundaries and migration ownership](../../adr/0014-persistence-foundation.md)
and [persistence setup](../data/persistence-foundation.md).

## Pipeline
`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

## Hard invariants
- Provider DTO != domain entity.
- Provider IDs do not live on canonical entities.
- Conflicts remain observable.
- `FeatureSnapshot` and `PredictionSnapshot` are immutable.
- `AsOfUtc` prevents temporal leakage.
- Core prediction is independent from bookmaker odds.
