# BetStats 2.0

Wielosportowa platforma danych, predykcji probabilistycznych, symulacji oraz operacji wspomaganych przez AI.

> **Status:** BS-011 — niezmienne symulacje historyczne, syntetyczne baseline'y, metryki i jawne recovery backtestów; realny provider pozostaje wyłączony.
> PostgreSQL przechowuje metadane ingestion, encje sportowe, wersjonowane polityki,
> audyt decyzji tożsamości i niezmienne obserwacje z paginacją. API liveness, Web i host Workera
> pozostają minimalne. Realne providery, wytrenowane predykcje, uwierzytelnianie
> i lokalizacja pozostają planowane.

## Lokalne API

```sh
dotnet run --project src/BetStats.Api --launch-profile http
```

Swagger: http://localhost:5000/swagger; OpenAPI: http://localhost:5000/swagger/v1/swagger.json.
W Visual Studio ustaw `BetStats.Api` jako projekt startowy, wybierz profil `http`
i uruchom debugowanie (F5). Profil automatycznie otwiera Swaggera. `BetStats.Web`
jest osobnym hostem demonstracyjnym i nie udostępnia dokumentacji API.
Dokumentacja działa tylko w Development. Publiczne odczyty: liveness i własny katalog sportów.
[Integralność historyczna, konfiguracja i granice API](docs/pl/data/historical-integrity-development-api.md).

## Granica produktu

[BS-011: backtesting i polecenia operatora](docs/pl/data/historical-backtesting.md)
opisuje syntetyczne predykcje i niezmienne raporty. Brak dowodów daje wykluczenia
i null zamiast zerowych metryk; nie twierdzimy, że zwalidowano realny model.
[ADR 0025](docs/adr/0025-historical-backtesting.md).

[BS-010: pokrycie wyników, operacje v3, recovery i koniec wydarzenia](docs/pl/data/result-coverage-operations-event-end.md)
opisuje jawne polecenia Development i migrację. Kompletność wymaga niezależnego
dowodu; BS-011 korzysta z tego kontraktu eligibility bez treningu modeli.
[ADR 0024](docs/adr/0024-result-coverage-operations-and-event-end.md).

[BS-009: wyniki, etykiety, zbiory v3 i fikcyjne API](docs/pl/data/football-results-outcomes.md)
opisuje jawne demo i addytywną migrację. Statystyki dotyczą częściowej historii;
nie trenujemy ani nie oceniamy modeli. [ADR 0023](docs/adr/0023-football-results-and-outcome-provenance.md).

[Zbiory danych i cechy BS-007](docs/pl/data/dataset-snapshots-features.md)
opisują jawne polecenia syntetyczne, weryfikację, porównanie i odzyskiwanie.
[ADR 0020](docs/adr/0020-dataset-snapshots-features.md). Cechy opisują częściową
zaobserwowaną historię; żaden model nie został wytrenowany ani zwalidowany.

[Workflow operatora i eligibility BS-006](docs/pl/data/quality-identity-reconciliation.md)
opisuje polecenia Workera, raporty, chroniony audyt i oddzielne tryby
HistoricalAsKnown/RetrospectiveReconstruction. [ADR 0019](docs/adr/0019-quality-review-reconciliation.md).
Operator jest deklarowany ręcznie; nie ma publicznego API administracyjnego.

BetStats **nie jest bukmacherem**. Nie przyjmuje prawdziwych stawek, depozytów ani wypłat i nie wykonuje zakładów. Prediction Playground korzysta wyłącznie z wirtualnych kuponów do analizy, edukacji i zabawy.

## Planowany pierwszy vertical slice

`Football → jedna rozgrywka → jeden dozwolony darmowy provider → RAW → Observation → Canonical → FeatureSnapshot → PredictionSnapshot → Evaluation`

Przed zmianami przeczytaj `AGENTS.md`. Dane zewnętrzne podlegają własnym licencjom i nie są objęte licencją kodu repozytorium.

## Wymagania i weryfikacja

Stabilny SDK .NET 10, minimum `10.0.100`. `global.json` dopuszcza nowsze pasma
funkcjonalne 10.0 (`latestFeature`), bez wersji prerelease. Restore wymaga dostępu
do NuGet.org. Z katalogu głównego repozytorium uruchom:

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
```

Wersje pakietów są zarządzane centralnie w `Directory.Packages.props`.
`Directory.Build.props` ustawia nullable, implicit usings, deterministyczne
kompilacje, analyzery SDK i traktowanie ostrzeżeń jako błędów.

## Struktura i testy

- `src/BetStats.Domain`: encje kanoniczne, reguły tożsamości i obserwacji; brak zależności projektowych.
- `src/BetStats.Application`: ocena praw, kontrakty/budżety providerów i historyczne zapytania; zależy od Domain.
- `src/BetStats.Infrastructure`: PostgreSQL, DbContext, mapowania i migracje; zależy od Application i Domain.
- `src/BetStats.Api` i `src/BetStats.Worker`: composition roots, mogą składać Application, Infrastructure i Domain.
- `src/BetStats.Web`: prezentacja; może zależeć od Application i Domain, bez Infrastructure i persystencji.
- `tests/BetStats.UnitTests`: reguły Domain/Application, UTC, dostępność, korekty i pierwszeństwo konfiguracji hostów.
- `tests/BetStats.ArchitectureTests`: sprawdza ocenione przez MSBuild zależności w Debug i Release, cykle i obejścia granic.
- `tests/BetStats.IntegrationTests`: migracje od zera i aktualizację BS-002, constraints, historię i wykluczenie przyszłych danych na rzeczywistym PostgreSQL.

Obowiązuje modular monolith oraz [ADR 0013](docs/adr/0013-project-dependency-direction.md).
Testy architektury wymagają checkoutu
źródeł i SDK. CI wykonuje restore, build Release i testy; nieudany test zatrzymuje
CI. CodeQL zachowuje ręczną kompilację z BS-000. Pełne testy wymagają Docker
z kontenerami Linux; CI jawnie uruchamia unit, architecture i integration na Ubuntu.
Brak Docker powoduje błąd testów integracyjnych, nie pominięcie.

## Praca lokalna i konfiguracja

Pracuj na gałęzi od `main`, w zakresie Issue. Dodaj testy i dokumentację,
uruchom powyższe polecenia i otwórz PR do `main`. Scalanie wymaga przeglądu.
Host uruchom np. przez `dotnet run --project src/BetStats.Api`; analogicznie
działają Worker i Web.

Domyślne hosty wczytują opcjonalne `appsettings.json`, JSON dla środowiska,
zmienne środowiskowe, a następnie argumenty polecenia. Zmienne nadpisują JSON,
a argumenty nadpisują zmienne. Zagnieżdżone klucze zapisuj z `__`, np.
`Logging__LogLevel__Default=Information`. Do pracy lokalnej ustaw
`DOTNET_ENVIRONMENT=Development` dla Workera lub
`ASPNETCORE_ENVIRONMENT=Development` dla API/Web.

Sekrety przekazuj przez środowisko; nie zapisuj ich w repozytorium. `.env`,
lokalne pliki ustawień, certyfikaty, wyniki kompilacji i lokalne dane są ignorowane.
`appsettings.*.local.json` nie są automatycznie wczytywane. .NET nie wczytuje
automatycznie `.env`; ten plik służy Docker Compose. `.env.example` zawiera
wyłącznie publiczne placeholdery deweloperskie.

## PostgreSQL i migracje

Skopiuj `.env.example` do ignorowanego `.env` (`Copy-Item .env.example .env`),
zmień hasło i zgodny connection string. Compose wymaga POSTGRES_PASSWORD;
baza/użytkownik/port są konfigurowalne. Port jest dostępny przez `127.0.0.1`,
a nazwany wolumen zachowuje dane po wyłączeniu.

```sh
docker compose up -d --wait postgres
docker compose ps
docker compose exec postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
docker compose down
```

Ustaw `ConnectionStrings__BetStats` w środowisku procesu API/Worker/EF;
.NET nie wczytuje automatycznie `.env`. PowerShell:
`$env:ConnectionStrings__BetStats = Read-Host 'Lokalny connection string' -MaskInput`.
Infrastructure rejestruje persystencję. Nie ma połączenia ani migracji przy
starcie hosta; pobranie DbContext wymaga konfiguracji.

```sh
dotnet tool restore
dotnet ef migrations list --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations script --idempotent --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --output artifacts/persistence.sql
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet test tests/BetStats.IntegrationTests --configuration Release --no-build
```

Przejrzyj SQL przed zastosowaniem. Schemat `ingestion` zawiera DataSources
(konfiguracja), IngestionRuns (wykonanie) i RawPayloads (niezmienne metadane RAW
i odwołanie do magazynu). Nie przechowuje bajtów payloadu.
Klucze UUID, czasy UTC `timestamp with time zone`, FK RESTRICT chronią historię.

Zwykłe `docker compose down` zachowuje dane. Aby **usunąć wszystkie lokalne dane**,
sprawdź projekt Compose, wykonaj `docker compose down --volumes`, uruchom bazę
i zastosuj migracje ponownie. Nie resetuj wspólnej/produkcyjnej bazy do testów.
Przyszłe ingestion wymaga SourcePolicy i praw do celu użycia; retencja musi
koordynować payloady i metadane przez jawny audytowany proces. Nie dodano purge job.
Szczegóły: [persystencja PostgreSQL](docs/pl/data/persistence-foundation.md),
[ADR 0014](docs/adr/0014-persistence-foundation.md).
EF Core 10, Npgsql i Testcontainers są zintegrowane; OpenTelemetry i ML pozostają planowane.

BS-003 dodaje sześć tabel `canonical` i trzy `provenance`, zachowując migrację
początkową oraz dane ingestion. UUID kanoniczny jest niezależny od identyfikatora
providera. Migracja dodaje cztery sporty referencyjne; dane testowe są syntetyczne.
Historia filtruje `AvailableAtUtc <= AsOfUtc` oraz od BS-004.1 `RecordedAtUtc <= AsOfUtc`
i sortuje po dostępności, utworzeniu
i UUID. Korekty tworzą nowe rekordy; późniejsze decyzje tożsamości nie zmieniają
wcześniejszych obserwacji. Bieżące encje kanoniczne nie są źródłem historii.
Zobacz [model danych, ER i ograniczenia](docs/pl/data/canonical-sports-model.md)
oraz [ADR 0015](docs/adr/0015-canonical-identity-and-temporal-observations.md).

BS-004 dodaje schemat `governance`: niezmienne wersje SourcePolicy, osobne
uprawnienia dla celów oraz audyt zatwierdzeń/cofnięć. Unknown odmawia; wewnętrzne
Approved nie dowodzi praw licencyjnych. Kontrakty providerów i executor sprawdzają
politykę przed wykonaniem; brak HTTP clients i realnego ingestion. Budżet procesu
ogranicza minutę/dobę/concurrency, timeout i Retry-After, bez automatycznych retries
i koordynacji wielu instancji.

Historia tożsamości wymaga teraz czasu DB `RecordedAtUtc` oraz czasu decyzji.
Stare rekordy BS-003 otrzymują dostępność z czasu migracji, więc wcześniejsze cutoffy
je wykluczają. Obserwacje mają paginację keyset, domyślnie do 200 rekordów
(`History__MaximumPageSize`, 1–1000). Stary odczyt listy zgłasza błąd przy przekroczeniu
limitu; dla większych wyników użyj `ReadPageAsOfAsync`.
Zobacz [governance, kontrakty i ograniczenia](docs/pl/data/source-governance.md)
oraz [ADR 0016](docs/adr/0016-source-governance-and-bounded-history.md).

BS-004.1 dodaje czas zapisu obserwacji nadawany przez bazę, bieżącą kontrolę źródła,
walidację odpowiedzi adapterów i bazodanową niezmienność RAW. Zobacz
[naprawy audytu, migrację i retencję](docs/pl/data/audit-remediation.md)
oraz [ADR 0017](docs/adr/0017-audit-remediation-and-trusted-observations.md).
Czas INSERT nie gwarantuje snapshotu według COMMIT.

BS-005 dodaje parser/adapter CSV fixture, filesystem RAW z SHA-256, czas zapisu DB,
audyt ingestii, jawne mapowania, EventDate oraz receipts publikacji partii/wierszy.
Football-Data.co.uk jest ocenionym kandydatem z ograniczeniami praw użycia, bez
zatwierdzonej polityki i transportu HTTP. Nie ma pobierania ani ingestii przy starcie.
Zobacz [demo, recovery i ograniczenia](docs/pl/data/first-football-ingestion.md),
[ocenę kandydata](docs/en/data/football-data-assessment.md) i
[ADR 0018](docs/adr/0018-first-football-ingestion.md).
Po jawnym zastosowaniu migracji w osobnej lokalnej bazie:

```sh
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
```

Zatwierdzenie dotyczy tylko fikcyjnej fixture. RAW musi być poza repozytorium;
plik/DB nie stanowią atomowej transakcji.

## Dokumentacja

BS-008 dodaje jawne pokrycie z przeglądem, pochodzenie czasu zdarzeń, manifesty v2
i przyszłe kontrakty ewaluacji. Snapshoty v1 pozostają niezmienne i czytelne.
Nie deklarujemy kompletności realnych danych ani jakości modeli. Zobacz
[semantykę, komendy i migrację](docs/pl/data/coverage-time-evaluation.md)
oraz [ADR 0021](docs/adr/0021-coverage-time-evaluation.md).

- 🇬🇧 [Software & Product Engineering Specification v1.0 — English](docs/specifications/BetStats_2_0_Software_Product_Engineering_Specification_v1_0_EN.docx)
- 🇵🇱 [Specyfikacja Produktu i Inżynierii v1.0 — Polski](docs/specifications/BetStats_2_0_Specyfikacja_Produktu_i_Inzynierii_v1_0_PL.docx)
- ADR: `docs/adr/`
- Dokumentacja angielska: `docs/en/`
- Dokumentacja polska: `docs/pl/`

Markdown jest dokumentacją żywą; pliki DOCX są wersjonowanym baseline specyfikacji.
