# Przegląd architektury

BetStats jest modular monolithem.

Domain nie zależy od innych projektów. Application zależy od Domain,
Infrastructure od Application i Domain. API i Worker są composition roots.
Web może zależeć od Application i Domain, bez Infrastructure i persystencji.
Testy architektury weryfikują graf w Debug i Release przez MSBuild.
Zobacz [ADR 0013](../../adr/0013-project-dependency-direction.md).
Pipeline opisuje architekturę docelową. BS-002 dodaje PostgreSQL i metadane
źródeł/run/RAW w Infrastructure, bez funkcji sportowych.
Zobacz [ADR 0014](../../adr/0014-persistence-foundation.md) i
[konfigurację persystencji](../data/persistence-foundation.md).

Pipeline:
`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

Najważniejsze niezmienniki: separacja provider/domain, zachowanie konfliktów i provenance, immutable snapshots, `AsOfUtc`, oddzielenie Prediction Engine od Market Engine.
