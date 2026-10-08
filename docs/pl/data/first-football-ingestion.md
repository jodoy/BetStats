# Pierwsza ingestia piłkarska (BS-005)

[ADR 0018](../../adr/0018-first-football-ingestion.md),
[pełny kontrakt EN](../../en/data/first-football-ingestion.md),
[ocena Football-Data.co.uk z 2026-10-08](../../en/data/football-data-assessment.md).

To pionowy wycinek na fikcyjnych danych. Kandydat zastrzega dane dla użytkowników
prywatnych i wyklucza produkty komercyjne/treningowe z botami/AI. Nie pobrano datasetu,
nie zatwierdzono polityki providera. Nie ma transportu HTTP. Ingestion__LiveEnabled=true
jest odrzucane nawet przy wewnętrznie zatwierdzonej polityce.

## Pipeline i prawa

Application zawiera use case i typowane rekordy, Infrastructure parser CSV,
fixture adapter, storage i PostgreSQL. Domain nie zawiera nazw kolumn providera.
Worker ma jawne polecenie demo, bez migracji/ingestii przy starcie i schedulera.

Najpierw zapisuje audyt próby, następnie używa istniejącego executora i wspólnego
budżetu. Zapis RAW ponownie sprawdza źródło, DataRetrieval, RawPayloadStorage i
HistoricalRetention; publikacja dodatkowo InternalAnalytics. Unknown, cofnięte
prawa i brak źródła odmawiają. Blokada źródła serializuje transakcje DB z audytem
polityk, ale nie tworzy transakcji plik/baza. Częściowy import ma Partial i szczegółowy
audyt, a w starszym IngestionRun status Failed. Nie oznaczamy go jako pełny sukces.

CSV jest UTF-8, do 1 MiB/5000 rekordów. Wymaga Div, Date, HomeTeam, AwayTeam;
obsługuje FTR i Time, ale nie odgaduje strefy/godziny kickoffu. EventDate jest DateOnly.
Kolumny wyników mogą być zachowane w RAW, ale wyników/statystyk/odds nie normalizujemy.
Nieznane nagłówki, niepoprawne wiersze i duplikaty mają jawne kody. Season pochodzi
z podanego kontekstu pliku, nie z wymyślonej kolumny. MatchId jest opcjonalnym
rozszerzeniem profilu fixture; nie przypisujemy go rzeczywistemu providerowi.

Zespoły mają odwołania z kontekstem źródła/rozgrywki, nie globalne nazwy. Competition,
Season i Participant wymagają jawnych decyzji operatora. Nieznane tożsamości zachowują
RAW i obserwacje bez celu; nie ma fuzzy matching. Odwołania złożone są oznaczone i
nie udają ID providera. Nie rozstrzygają automatycznie przełożonej daty ani dwóch
spotkań tych samych drużyn jednego dnia. Sprzeczne mapowania są odrzucane.

## RAW, czas i powtórzenia

Oryginalne bajty trafiają do pliku key.pending, SHA-256 do metadanych, a finalizacja
przenosi plik do key.raw bez nadpisania. Odczyt weryfikuje hash i rozmiar. Storage
ma konfigurowalny absolutny katalog poza repozytorium, bez linków/traversal; nie jest
produkcyjnym object storage ani ochroną przed uprzywilejowanym administratorem.

RAW RecordedAtUtc i Observation RecordedAtUtc są odrębnymi czasami INSERT DB.
Stary RAW otrzymuje czas migracji, bez zgadywania historycznej dostępności; stare
daty pozostają, ByteLength jest null. Normalizacja używa bieżącej dostępności,
a AsOfUtc nadal sprawdza oba cutoffy. INSERT nie oznacza COMMIT/snapshotu.

Klucz publikacji obejmuje źródło, rozgrywkę, sezon, hash bajtów i wersję parsera.
Poprawny powtórzony payload ma Reused; nowe próby/run/RAW nadal są audytowane.
Partial można ponowić po review; niezmienione fakty nie dublują się, zmiany dopisują
korekty. Receipt poprawnych wierszy chroni też przed cofnięciem późniejszej korekty
przez ponowienie starego Partial. Konflikt wierszy z jednym ID odrzuca oba warianty.
Starsze obserwacje nie są przemapywane po późniejszym review.

Nie ma atomowości plik/DB. IngestionRecovery jawnie uzgadnia staging z metadanymi
po weryfikacji praw i hasha. Osierocone pliki pozostają do autoryzowanego maintenance,
bez purge. Operator może oznaczyć przerwaną próbę dopiero po potwierdzeniu, że jej
właściciel nie działa. Receipt pozwala rozpoznać zakończoną publikację. Awaria DB
może uniemożliwić audyt końcowy; pozostaje Running i manifest do późniejszego recovery.
Wyniki maintenance należy zachować w chronionym audycie operatora.

## Demo lokalne

Użyj osobnej bazy developerskiej i prywatnego katalogu RAW poza repozytorium.
ConnectionStrings__BetStats ustaw bezpiecznie według [instrukcji](persistence-foundation.md).
Opcjonalnie ustaw Ingestion__RawStoragePath; domyślny katalog to LocalApplicationData/BetStats/raw.

```sh
dotnet tool restore
dotnet ef database update --project src/BetStats.Infrastructure
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
```

Jawna flaga --approve-synthetic zatwierdza wyłącznie autorską fikcyjną fixture i jej
dokładne mapowania. Nie zatwierdza realnego providera; istniejąca polityka nie jest
automatycznie naprawiana. Cztery próby pokazują Partial, powtórzenie Partial bez
duplikacji, korektę daty Succeeded i ponowienie Reused. Dane obejmują jedną fikcyjną
ligę/sezon, cztery zespoły, błędną datę, duplikat i nierozstrzygniętego uczestnika.

Zapytania kontrolne do IngestionRuns, IngestionAuditEvents, RawPayloads i Observations
są w dokumentacji EN. Sprawdź key.raw pod skonfigurowanym root i hash Get-FileHash
lub sha256sum. Nie commituj payloadów ani poświadczeń. Testy same usuwają własne
kontenery i katalogi tymczasowe. Nowe czyste demo najlepiej uruchomić w nowej bazie
i katalogu; reset wyłącznie lokalnego wolumenu opisuje dokumentacja persystencji.
Nie resetuj współdzielonych danych ani nie wyłączaj ochrony RAW.

## Migracja i następny krok

20261008083514_FirstFootballIngestion jest addytywna: czas/rozmiar RAW, EventDate,
audyt prób i receipt publikacji. Zachowuje historię i ochronę RAW. Backfill może
przepisać tabelę i wymagać silnych blokad; przejrzyj SQL, backup i okno maintenance.
Rollback traci nowy audyt i semantykę EventDate. Nie ma migracji przy starcie.

BS-006 powinno objąć narzędzia review/reconciliation i chroniony audyt albo jednego
osobno dozwolonego dostawcę z bezpiecznym transportem. Dzisiejsze dowody nie pozwalają
włączyć tego kandydata do zastosowań komercyjnych lub treningu.
