# Initial Backlog

## E01 Foundation
- BS-001 Bootstrap .NET 10 solution and repository.
- BS-002 Docker/PostgreSQL development environment.
- BS-010 OpenTelemetry baseline.
- BS-011 Security baseline.

## E02 Sports Core
- BS-003 Canonical Sports Core.
- BS-004 ExternalIdentity.

## E03 First Provider
- BS-005 RAW persistence.
- BS-006 First permitted free football provider.
- BS-007 Observation normalization.
- BS-008 Canonical resolver.
- BS-009 Golden Dataset.

## E05/E06 Prediction
- BS-012 FeatureSnapshot.
- BS-013 Elo baseline.
- BS-014 PredictionSnapshot.
- BS-015 Historical evaluation.

### First vertical-slice exit
One legal real event traverses:
`RAW → Observation → Canonical → FeatureSnapshot → PredictionSnapshot → Evaluation`
with trace ID, audit lineage and automated tests.
