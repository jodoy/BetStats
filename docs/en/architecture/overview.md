# Architecture Overview

BetStats is a modular monolith.

BS-006 adds provider-neutral quality evidence in Domain, versioned football rules
and operator ports in Application, and persistence adapters in Infrastructure.
See [ADR 0019](../../adr/0019-quality-review-reconciliation.md) and
[operator/historical contracts](../data/quality-identity-reconciliation.md).

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
BS-005 now implements fixture ingestion only; prediction and live provider transport
remain planned. See [ADR 0018](../../adr/0018-first-football-ingestion.md) and the
[licensed-boundary pipeline](../data/first-football-ingestion.md).
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

BS-004 adds source governance in Domain, policy evaluation/provider contracts and
in-process request budgets in Application, and governance persistence in
Infrastructure. SourcePolicy approval remains an internal review, distinct from
verified contractual rights. Historical identity reads now use trusted database
availability; observation reads have bounded keyset pages. No provider HTTP client
or endpoint is added. See [ADR 0016](../../adr/0016-source-governance-and-bounded-history.md)
and [governance and temporal limits](../data/source-governance.md).

`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

## Hard invariants

BS-007 adds bounded Application dataset contracts and pure feature calculators.
Infrastructure assembles time-bounded RAW/identity/quality evidence under REPEATABLE
READ, then finalizes immutable PostgreSQL artifacts/vectors after current rights
checks under shared source locks. See [ADR 0020](../../adr/0020-dataset-snapshots-features.md)
and [dataset commands and diagram](../data/dataset-snapshots-features.md).

- Provider DTO != domain entity.
- Provider IDs do not live on canonical entities.
- Conflicts remain observable.
- `FeatureSnapshot` and `PredictionSnapshot` are immutable.
- `AsOfUtc` prevents temporal leakage.
- Core prediction is independent from bookmaker odds.
