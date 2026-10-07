# Przegląd architektury

BetStats jest modular monolithem.

Pipeline:
`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

Najważniejsze niezmienniki: separacja provider/domain, zachowanie konfliktów i provenance, immutable snapshots, `AsOfUtc`, oddzielenie Prediction Engine od Market Engine.
