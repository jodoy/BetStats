# Wyniki piłkarskie, pochodzenie etykiet i statystyki historyczne (BS-009)

Szczegóły kontraktów: [dokumentacja angielska](../../en/data/football-results-outcomes.md)
i [ADR 0023](../../adr/0023-football-results-and-outcome-provenance.md).

Profil `results-v1` obsługuje wyłącznie jawnie dostarczone, fikcyjne CSV.
Dotychczasowy `metadata-v1` zachowuje semantykę i klucze publikacji. Kontekst ligi
i sezonu jest zapisany z RAW przed parsowaniem, a tożsamości wymagają przeglądu.
Nie pobieramy danych realnego dostawcy ani nie trenujemy modeli.

Statusy: Scheduled, Live, HalfTime, Finished, Postponed, Cancelled, Abandoned.
Obsługiwane okresy regulaminowe: HalfTime i FullTime. Brakujące wyniki pozostają
null; nie dopowiadamy wyniku do przerwy. Pary goli muszą być kompletne albo całkowicie
nieznane. Wyniki z dogrywki, karnych lub o nieznanej podstawie nie tworzą etykiet
regulaminowych. Finished bez pełnego wyniku nie kwalifikuje się do analizy wyniku.

Korekty dopisują wersję i poprzednika. Równoczesne sprzeczne raporty i rozbieżności
źródeł pozostają konfliktami. Duplikaty są idempotentne; Finished/Cancelled nie wraca
do Scheduled. Zapytania wymagają jawnego AsOfUtc oraz zarówno AvailableAtUtc, jak
i zaufanego RecordedAtUtc <= cutoff. Sprawdzają historyczną tożsamość, jakość,
oryginalny kontekst, hash RAW, uprawnienia źródła i bieżącą retencję.

Artefakty wynikowe v3 są oddzielne od istniejących v1/v2. Zamrażają metadane,
pokrycie, wyniki i ich pochodzenie. Cechy dotyczą tylko obserwowanych zakończonych
spotkań z 30 dni przed dniem predykcji. Nie przedstawiają częściowej próbki jako
pełnego sezonu. Wskaźniki są dokładnymi ułamkami; brak danych jest null, nie zerem.
Etykiety wyniku docelowego mają osobny cutoff i mogą przyjść po predykcji, ale nie
wchodzą do cech. Istniejące hashe i bajty snapshotów pozostają niezmienione.

## Uruchomienie lokalne

Ustaw lokalnie `ConnectionStrings__BetStats` i `Ingestion__RawStoragePath` poza
repozytorium. Nie zapisuj haseł w źródłach. Docker jest wymagany dla testów integracyjnych.
Migracje są uruchamiane ręcznie, nigdy przy starcie API:

```powershell
dotnet ef database update --project src/BetStats.Infrastructure
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Worker --configuration Release -- --Results:Action=demo --approve-synthetic
dotnet run --project src/BetStats.Api --launch-profile http
```

Demo jawnie zatwierdza uprawnienia wyłącznie własnych fikstur i importuje 2:1,
0:0, nieznany wynik do przerwy, przełożenie/anulowanie/zaplanowanie oraz późniejszą
korektę 2:2. Powtarza importy, pokazując ponowne użycie publikacji.
Fikstura opisuje fikcyjny sezon 2026, nie rzeczywiste przyszłe mecze.

Swagger: `http://localhost:5000/swagger`. Tylko Development udostępnia odczyty:

Potwierdzony wynik do przerwy może udostępnić wyłącznie `HalfTimeTotalGoals`.
Etykiety pełnego czasu pozostają `null` do potwierdzenia końcowego wyniku;
obserwacja do przerwy nie wchodzi do statystyk ukończonych meczów.

- `GET /api/v1/dev/events`
- `GET /api/v1/dev/events/{id}`
- `GET /api/v1/dev/events/{id}/result`
- `GET /api/v1/dev/events/{id}/history`

Wymagany parametr `asOfUtc`, np. `2026-10-08T18:00:00.000000Z`. Lista i historia:
`offset` domyślnie 0 (maksimum 10000), `limit` domyślnie 20 (1..100). Sortowanie
po UUID i dostępności. Błędne parametry: 400; brak kwalifikującego się wydarzenia:
404; odmowa polityki lub retencji: 403 bez danych; brak konfiguracji developerskiej: 503. W Production wszystkie te trasy i
Swagger zwracają 404. Dostęp wymaga PublicDisplay i dokładnego hasha zarejestrowanej
fikstury; odpowiedzi nie ujawniają RAW ani wewnętrznego audytu.

Migracja `20261008171605_FootballResultsOutcomeProvenance` dodaje niezmienne wyniki
i artefakty v3. Blokuje zwykłe UPDATE, DELETE i TRUNCATE. Role runtime nie mogą być
właścicielem bazy (ADR 0017). Builder v3 jest portem aplikacji; nie ma jeszcze
osobnego polecenia operatorskiego ani ledgeru odzyskiwania błędów jego składania.
BS-010 powinno uzupełnić pokrycie wyników, jawne dowody końca wydarzenia, obsługę
operatorską v3 i fikstury niezależne od roku. Brak ML, scoringu i wdrożenia produkcyjnego.
