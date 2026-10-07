# Architecture Overview

BetStats is a modular monolith.

## Pipeline
`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

## Hard invariants
- Provider DTO != domain entity.
- Provider IDs do not live on canonical entities.
- Conflicts remain observable.
- `FeatureSnapshot` and `PredictionSnapshot` are immutable.
- `AsOfUtc` prevents temporal leakage.
- Core prediction is independent from bookmaker odds.
