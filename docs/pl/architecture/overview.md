# Przegląd architektury

BetStats jest modular monolithem.

Domain nie zależy od innych projektów. Application zależy od Domain,
Infrastructure od Application i Domain. API i Worker są composition roots.
Web może zależeć od Application i Domain, bez Infrastructure i persystencji.
Testy architektury weryfikują graf w Debug i Release przez MSBuild.
Zobacz [ADR 0013](../../adr/0013-project-dependency-direction.md).
Pipeline opisuje architekturę docelową. BS-002 dodaje PostgreSQL i metadane
źródeł/run/RAW. BS-003 dodaje ogólny model kanoniczny i niezmienną historię
provenance; ingestion od providerów oraz predykcje pozostają planowane.
Zobacz [ADR 0014](../../adr/0014-persistence-foundation.md) i
[konfigurację persystencji](../data/persistence-foundation.md).

## Granice modelu i historii (BS-003)

Domain zawiera Sport, Competition, Season, Participant, SportingEvent,
EventParticipant oraz reguły jawnych decyzji tożsamości i typowanych obserwacji.
Application udostępnia `IObservationHistory` i `IIdentityResolutionHistory`.
Infrastructure implementuje filtry czasowe, mapowania EF i migracje addytywne.
API/Worker rejestrują kontrakty bez połączenia i migracji przy starcie.
Web nie zależy od persystencji. Nie dodano endpointów ani zadań ingestion.

Identyfikatory providerów są zakotwiczone w źródle, oddzielnie od UUID kanonicznych.
Wersjonowane decyzje dopuszczają unresolved/ambiguous bez zgadywania celu.
Encje kanoniczne opisują stan bieżący; historia używa filtrów dostępności
obserwacji i utrwalonych celów, bez joinów z bieżącymi projekcjami.
Constraints i triggery PostgreSQL chronią historię także przed zbiorczym SQL.
Zobacz [ADR 0015](../../adr/0015-canonical-identity-and-temporal-observations.md)
i [model, kontrakt czasowy oraz ER](../data/canonical-sports-model.md).

Pipeline:

BS-004 dodaje governance źródeł w Domain, ocenę praw, kontrakty providerów i
budżety procesu w Application oraz persystencję governance w Infrastructure.
Wewnętrzne zatwierdzenie nie oznacza praw licencyjnych. Historia tożsamości
używa dostępności DB; obserwacje mają ograniczone strony keyset. Brak klienta HTTP
i endpointów. Zobacz [ADR 0016](../../adr/0016-source-governance-and-bounded-history.md)
oraz [governance i ograniczenia czasu](../data/source-governance.md).

`Provider → RAW → Observation → Validation → Resolution → Canonical → FeatureSnapshot → DatasetSnapshot → Model → PredictionSnapshot → Signal`

Najważniejsze niezmienniki: separacja provider/domain, zachowanie konfliktów i provenance, immutable snapshots, `AsOfUtc`, oddzielenie Prediction Engine od Market Engine.
