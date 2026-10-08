# BS-010: pokrycie wyników, operacje v3 i koniec wydarzenia

BS-010 startuje z main `5678dc2`, po potwierdzonym merge PR #13 (BS-009).
[ADR 0024](../../adr/0024-result-coverage-operations-and-event-end.md) opisuje decyzje;
[pełny runbook angielski](../../en/data/result-coverage-operations-event-end.md) zawiera kontrakty i argumenty.

Pokrycie wyników jest niezależne od metadanych. Zakres określa źródło, rozgrywki,
sezon, opcjonalnego uczestnika i przedział kalendarza UTC [początek, koniec).
Unknown oznacza brak uzasadnionego zatwierdzenia, Partial ograniczony dowód,
Complete kompletny zamknięty fikcyjny inwentarz, Empty jawnie pusty inwentarz,
Conflict sprzeczność, a Expired wygaśnięcie dowodów. Brak danych nie oznacza zera.
Silne twierdzenia wymagają własnej fikstury RAW, poprawnej tożsamości i jakości,
aktualnych praw oraz osobnego review. Metadane ukończonego meczu bez wyniku
uniemożliwiają Complete/Empty. Nie certyfikujemy przyszłych przedziałów ani realnych dostawców.
Późniejsze review nie poprawia wcześniejszego HistoricalAsKnown; rekonstrukcja ma
osobny jawny czas. Korekta wyniku wymaga nowego, zatwierdzonego inwentarza.

Koniec wydarzenia jest jawnym dowodem RAW związanym z oryginalnym wynikiem,
kontekstem sezonu, referencją wydarzenia i zatwierdzoną tożsamością. Nie obliczamy
go ze startu i stałego czasu trwania. DateOnly/Unknown pozostają niepewne.
Definicja ewaluacji v3 wymaga precyzji minuty/sekundy, dowodu znanego do czasu
ewaluacji i niezależnego Complete dla wyników. To kontrakt kwalifikacji, bez modeli i scoringu.

Operacja v3 ma jawny UUID i fingerprint żądania. Rejestr jest dopisywany:
Requested -> Running -> Succeeded/Failed/Cancelled. Running dostaje token właściciela
i dziesięciominutową dzierżawę zegara PostgreSQL. Recovery nie przejmuje żywego
właściciela; wymaga zgodnego fingerprintu i jawnego zatwierdzenia. Publikacja
artefaktu i Succeeded są atomowe; stary wykonawca po wygaśnięciu nie może publikować.
Nie ma automatycznego ponawiania ani odnowienia dzierżawy. Awaria bazy może zostawić Running.
Istniejące bajty i hashe v1/v2/v3 pozostają bez zmian; nowe operacje zamrażają
opcjonalny dodatek governance v1. Statystyki nadal opisują częściowo obserwowaną historię.

Skonfiguruj lokalne `ConnectionStrings__BetStats` i bezwzględny
`Ingestion__RawStoragePath` poza repozytorium. Migracje wykonuj jawnie:

```powershell
dotnet ef database update --project src/BetStats.Infrastructure
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Worker --configuration Release -- --Results:Action=demo-clock --Results:SourceCode=synthetic-clock-demo --Results:OperatorId=operator:local --Results:Reason="Explicit owned fixture demo" --Results:Approve=true
```

Nowe fikstury zależą od jawnego TimeProvider i działają również na granicy roku.
Zmiana dnia scenariusza wymaga nowego SourceCode, zamiast nadpisywania historii.
Stare demo BS-009 zachowuje oryginalne bajty i publiczną listę hashy z 2026.
Nowe demo nie otrzymuje prawa PublicDisplay.

Akcje Worker: `capture-evidence`, `record-inventory`, `review-inventory`, `coverage`,
`record-end`, `ends`, `build-v3`, `operation`, `recover-v3`, `inspect-v3`,
`verify-v3`, `verify-v3-deep`. Wszystkie wymagają aktora i powodu; modyfikacje także
`Results:Approve=true`. Najpierw przechwyć lokalny JSON dowodu do RAW, potem użyj
zwróconego UUID w kontrakcie. Nie wymyślaj referencji, wyniku, czasu ani uprawnień.
Identyfikator aktora jest deklaracją audytową, nie uwierzytelnieniem.
Deep verification rozróżnia integralność snapshotu od dostępności i hasha RAW;
cofnięcie praw uniemożliwia odczyt bajtów.

Migracja `20261008221019_ResultCoverageOperationsEventEnd` dodaje cztery chronione
tabele; zwykłe UPDATE/DELETE/TRUNCATE są blokowane. Konto runtime nie może być
właścicielem tabel. Startup nie wykonuje migracji, seedowania, importu ani recovery.
Brak realnych integracji, treningu, nowych produkcyjnych API i interfejsu użytkownika.
