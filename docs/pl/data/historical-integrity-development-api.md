# Integralność historyczna i lokalne API

BS-008.1 naprawia sześć ustaleń audytu. [ADR 0022](../../adr/0022-historical-integrity-and-development-api.md)
i [pełna dokumentacja angielska](../../en/data/historical-integrity-development-api.md)
opisują wersje kontraktów i ograniczenia.

- Oryginalny kontekst pliku jest zapisywany przy przechwyceniu RAW i niezmienny.
  Dataset nie może zmienić sezonu przez podanie nowej definicji. Starsze receipt’y
  mogą potwierdzić kontekst; brak wiarygodnych dowodów blokuje użycie.
- Pokrycie porównuje wyłącznie fakty ze wspólnego przedziału [start, end).
  Różne zakresy i pusty podprzedział nie oznaczają automatycznie sprzeczności.
- Lookback N dni ma dokładnie zakres [D-N, D), gdzie D to dzień UTC predykcji
  wskazany przez AsOfUtc. Przedziały przyszłe i przesunięte są odrzucane.
- Horyzont ewaluacji oznacza minimalny czas wyprzedzenia zdarzenia. DateOnly nie
  stanowi godziny rozpoczęcia. Wymagane są powiązane dowody czasu i tożsamości.
- Precyzyjny czas wymaga koperty RAW z referencją meczu i oryginalnego ingestion.
  Starsze niepowiązane twierdzenia operatora pozostają w historii, lecz nie dają
  uzasadnionej chwili UTC.
- Standardowa weryfikacja rozdziela integralność artefaktu, kompletność metadanych,
  bieżące prawa i odtwarzalność cech. RawAvailable/RawHashVerified są null bez
  odczytu. Opcjonalne verify-deep sprawdza prawa i retencję przed odczytem RAW,
  następnie długość i SHA-256. Brak RAW nie zmienia skrótu zapisanego artefaktu.

Nie zmieniamy zapisanych artefaktów v1/v2 ani opublikowanych migracji. Nowa
migracja 20261008150019_HistoricalIntegrityContext jest addytywna; nie odgaduje
kontekstu starszych danych ani nie zmienia historycznych timestampów.

## Uruchomienie

Wymagany stabilny SDK .NET 10, minimum 10.0.100; global.json dopuszcza nowsze
feature bandy .NET 10. Z katalogu repozytorium:

```powershell
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
dotnet run --project src/BetStats.Api --launch-profile http
```

Profil http jawnie ustawia Development oraz http://localhost:5000:

- Swagger: http://localhost:5000/swagger
- OpenAPI: http://localhost:5000/swagger/v1/swagger.json
- Liveness: GET /health/live oraz GET /api/v1/health.
- Katalog własnych kodów sportów: GET /api/v1/sports?offset=0&limit=20.

Offset: 0..10000, limit: 1..100. Dane są uporządkowane według kodu. Strona poza
czterema rekordami jest pusta. Błędne parametry zwracają 400 ProblemDetails.
Liveness i katalog działają bez konfiguracji bazy; pusta baza nie zmienia odpowiedzi.
Liveness nie jest testem gotowości PostgreSQL.

W Production Swagger i OpenAPI są niedostępne, a HTTPS redirection pozostaje
włączone. Nie udostępniamy mutacji administracyjnych, RAW ani audytu. Endpointy
meczów, sezonów, rozgrywek, datasetów i pokrycia pominięto, ponieważ istniejące
zapytania operatorskie nie zapewniają bezpiecznej autoryzacji anonimowego
PublicDisplay. Obecność danych w tabeli kanonicznej nie daje prawa publikacji.

## PostgreSQL i dane syntetyczne

Do operacji persystencji oraz testów integracyjnych potrzebne są PostgreSQL 17
i Docker z kontenerami Linux. [Instrukcja konfiguracji](persistence-foundation.md)
opisuje docker compose i ignorowany .env. .NET nie czyta .env automatycznie.
Ustaw własne dane dostępowe lokalnie:

```powershell
$env:ConnectionStrings__BetStats = Read-Host 'Lokalny connection string PostgreSQL' -MaskInput
dotnet tool restore
dotnet ef database update --project src/BetStats.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

API nie wykonuje migracji ani ingestion przy starcie. Ingestion__RawStoragePath
może wskazywać katalog absolutny poza repozytorium; domyślnie używane są lokalne
dane aplikacji BetStats/raw. Nie commituj poświadczeń ani RAW.

```powershell
dotnet run --project src/BetStats.Worker -- --synthetic-demo --approve-synthetic
```

To jawna inicjalizacja fikcyjnej próbki. [Polecenia datasetów](dataset-snapshots-features.md)
wymagają operatora i powodu; Dataset:Action=verify-deep uruchamia głęboką weryfikację.
Dane syntetyczne nie nadają praw do providerów i nie są publicznie udostępniane.
BS-009 powinien potwierdzić kontrakt dozwolonego providera i pochodzenie wyników
przed wprowadzeniem modeli lub wykonawcy ewaluacji.
