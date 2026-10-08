# Fundament persystencji PostgreSQL

BS-002 dodaje metadane źródeł, przebiegów ingestion i przechwyceń RAW.
EF Core 10, Npgsql, BetStatsDbContext i migracje należą do Infrastructure.
API i Worker wywołują `AddPersistence`. Rejestracja i utworzenie kontekstu nie
łączą się z bazą. Nie ma migracji przy starcie ani `EnsureCreated`.
Zobacz [ADR 0014](../../adr/0014-persistence-foundation.md).

Ta strona opisuje ingestion z BS-002. BS-003 dodaje oddzielne schematy canonical
i provenance: [model oraz kontrakt historii](canonical-sports-model.md).
BS-004.1 dodaje bazodanową ochronę RAW i projekt kontrolowanej retencji:
[naprawy audytu](audit-remediation.md).
BS-004 dodaje [polityki, audyt i dostępność tożsamości](source-governance.md)
przez migrację addytywną; definicje ingestion pozostają zachowane.

## Schemat i provenance

Tabele znajdują się w schemacie `ingestion`, historia migracji EF w `public`.

| Tabela | Znaczenie | Ograniczenia |
| --- | --- | --- |
| DataSources | Konfiguracja źródła: UUID, kod, nazwa, aktywność, czas utworzenia | Niepusty unikalny Code (case-sensitive, 100 znaków), niepusta nazwa (200 znaków) |
| IngestionRuns | Wykonanie: źródło, status, opcjonalne czasy start/koniec i ErrorCode | FK do źródła; Pending/Running/Succeeded/Failed; koniec nie wcześniejszy od wymaganego startu |
| RawPayloads | Niezmienne metadane przechwycenia: źródło, opcjonalny run i ExternalReference, czasy, SHA-256, ContentType, StorageKey | FK do źródła i złożony FK run/źródło; 64 małe znaki hex; niepuste ContentType/StorageKey |

Wszystkie FK używają RESTRICT, bez kasowania historii przez cascade-delete.
Złożony FK uniemożliwia wskazanie runu z innego źródła. Indeksy obejmują kod
źródła, źródło/czas, run/źródło i hash. Powtórzenie tego samego hasha jest
dozwolone: każda obserwacja pobrania pozostaje osobnym rekordem.
UUID i czasy podaje wywołujący. Wymagany DateTime UTC jest mapowany na
`timestamp with time zone` z dokładnością mikrosekund. Kontekst odrzuca inne
rodzaje DateTime oraz aktualizacje/usuwanie RawPayload. Od BS-004.1 triggery blokują
zwykłe UPDATE/DELETE/TRUNCATE RAW, również SQL i EF bulk. Administrator może je
ominąć; ochrona nie obejmuje zewnętrznego magazynu payloadów.

ExternalReference to identyfikator źródła, nie przyszły kanoniczny identyfikator
sportowy. StorageKey jest nieprzezroczystym odwołaniem, bez podpisanych URL,
kluczy API i payloadu. ErrorCode zawiera klasyfikację, nie treść odpowiedzi ani
sekrety. Nie zaimplementowano magazynu obiektowego, providerów ani schedulera.

Przyszłe ingestion wymaga SourcePolicy i praw do konkretnego celu. Konfiguracja
źródła nie nadaje prawa do przechowywania, treningu, wyświetlania, redystrybucji
czy wykorzystania komercyjnego. RAW -> Observation -> Canonical, encje sportowe,
AsOfUtc i snapshoty predykcji pozostają poza zakresem.

## PostgreSQL lokalnie

Docker musi działać z kontenerami Linux. Z katalogu głównego:

```powershell
Copy-Item .env.example .env
# Edytuj ignorowany .env: hasło lokalne i zgodny connection string.
docker compose up -d --wait postgres
docker compose ps
docker compose exec postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
docker compose logs --tail 50 postgres
docker compose down
```

W Bash użyj `cp .env.example .env`. Compose odczytuje POSTGRES_DB, POSTGRES_USER,
wymagany POSTGRES_PASSWORD i opcjonalny POSTGRES_PORT (5432). Port jest przypięty
do 127.0.0.1; dane pozostają w nazwanym wolumenie `betstats-postgres`.
Health check sprawdza dostępność serwera, nie aktualność schematu.
Zmiana POSTGRES credentials nie zmienia kont w istniejącym wolumenie.

.NET nie odczytuje `.env` automatycznie. W środowisku procesu hosta/narzędzia EF
ustaw `ConnectionStrings__BetStats` ze zgodnym portem, bazą, użytkownikiem i hasłem:

```powershell
$env:ConnectionStrings__BetStats = Read-Host 'Lokalny connection string PostgreSQL' -MaskInput
```

W Bash użyj `read -r -s -p 'Lokalny connection string: ' ConnectionStrings__BetStats`
i `export ConnectionStrings__BetStats`. Nie zapisuj wartości w historii poleceń.
Design-time factory używa zmiennych środowiskowych, bez JSON i `.env`.
Bez connection string można generować model/SQL offline; aktualizacja bazy go wymaga.
API liveness i Worker mogą wystartować bez skonfigurowanej bazy; pobranie DbContext
wymaga konfiguracji. Przy starcie nie ma próby połączenia.

## Migracje

```sh
dotnet tool restore
dotnet ef migrations list --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations script --idempotent --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --output artifacts/persistence.sql
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
```

Przejrzyj SQL przed zastosowaniem. Database update wymaga jawnej konfiguracji
lokalnego połączenia i uprawnień DDL. Nową zmianę schematu dodawaj przez
`dotnet ef migrations add <Name>` z tymi samymi opcjami project/startup-project;
commituj migrację i snapshot, przejrzyj diff i uruchom testy. Generowanie migracji
i SQL nie wymaga działającej bazy. Nie stosuj credentials produkcyjnych.
Migracja początkowa Down usuwa tabele i nie zastępuje procesu retencji.

## Testy i CI

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
dotnet test tests/BetStats.IntegrationTests --configuration Release --no-build
```

Testcontainers uruchamia jednorazowy `postgres:17-alpine`, losowy port i hasło,
bez współdzielonego wolumenu. Testy nie odczytują connection string dewelopera
ani produkcji. Migracja tworzy świeżą bazę, dane testowe są deterministyczne
i wycofywane transakcją, a zasoby są zwalniane. Testy obejmują schemat,
constraints, zapis/odczyt, UTC, niezmienność RAW, ochronę historii i DI bez połączenia.
Brak Docker powoduje błąd, nie pominięcie. CI na Ubuntu wykonuje jawnie unit,
architecture i integration; CodeQL jest zachowane.

## Reset i retencja

Poniższe polecenia usuwają **wszystkie lokalne dane** projektu Compose.
Najpierw sprawdź projekt i upewnij się, że dane nie wymagają zachowania:

```sh
docker compose down --volumes
docker compose up -d --wait postgres
# Ustaw ConnectionStrings__BetStats i zastosuj migracje.
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
```

Zwykłe `docker compose down` zachowuje wolumen. Testy nie potrzebują resetu.
Nigdy nie resetuj wspólnej/produkcyjnej bazy do testów. Retencja musi respektować
licencje i prywatność: przyszły jawny, audytowany proces skoordynuje usuwanie
payloadów z magazynu i metadanych. RESTRICT wymaga świadomej obsługi zależności.
Nie dodano zadania czyszczącego. Nie commituj danych providerów, dumpów ani
sekretów. Role/uprawnienia migracyjne i hosting produkcyjny pozostają przyszłą pracą.
