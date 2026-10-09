# BS-015 — instrukcja operatora

Dispatcher działa wyłącznie w Development i wymaga jawnego wywołania. Start API, Web lub Worker nie uruchamia zadań ani migracji. Nie ma publicznych endpointów administracyjnych. Actor i Reason są deklaracjami audytowymi, a nie uwierzytelnioną tożsamością.

## Przygotowanie i polecenia

Zastosuj migracje jawnie do zatwierdzonej bazy PostgreSQL. Ustaw `DOTNET_ENVIRONMENT=Development`, połączenie w lokalnej konfiguracji sekretów oraz magazyn RAW poza repozytorium. Do weryfikacji używaj zatwierdzonych, autorskich danych fikcyjnych. Transport HTTP pozostaje niedostępny.

Definicja ma Version=1, Kind (`LocalSynchronization`, `PrematchPrediction`, `PostmatchEvaluation`), Schedule, PayloadJson, MaximumAttempts (1–5), LeaseSeconds (1–1800) i PredictionHorizonSeconds (domyślnie 43200). PayloadJson jest **ciągiem JSON zawierającym obiekt**. Schematy payloadów i pełne przykłady opisuje [instrukcja angielska](automated-pipeline.en.md).

Schedule wymaga FirstDueUtc w UTC, TimeZone="UTC", DaylightSavingPolicy="NotApplicable" i opcjonalnego IntervalSeconds (60–2592000). Termin prematch wylicz z rzeczywiście potwierdzonego kickoff minus wybrany horyzont. Nie zgaduj strefy, DST, hashy ani historycznych zegarów. Dla pliku lokalnego najpierw uzyskaj plan BS-013 przez FootballHistory:Action=plan. Osadź jego Result jako Plan oraz bezwzględną ścieżkę jako Path. Plik definicji przechowuj lokalnie.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$job = '<UUID zadania>'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved synthetic workflow', '--Pipeline:Approve=true')
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=C:\approved-inputs\job.json @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=inspect --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=status @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=disable --Pipeline:JobId=$job @audit
```

Plan zapisuje wyłączoną wersję. Enable nie uruchamia procesu w tle. Run-once pobiera najwyżej jedno włączone zadanie, którego termin minął według zegara PostgreSQL. Pusta kolejka kończy się bez wymyślonego wykonania. Wywołuj dispatcher jawnie z potrzebną częstotliwością; kilka workerów może obsługiwać różne zadania. Kolejne terminy wynikają z pierwotnego harmonogramu UTC; pominięte wystąpienia nie są odtwarzane jako historyczne wykonania. Disable blokuje przyszłe pobrania, a aktywne wykonanie wymaga osobnego cancel.

Polecenia odczytu wymagają Actor i Reason. Każda mutacja wymaga dodatkowo Approve=true. Inspect/status nie pokazują payloadów, ścieżek, powodów operatora ani treści dostawcy.

Jawne `Pipeline:Action=work` włącza polling co sekundę. Wymaga tej samej zgody audytowej, DurationSeconds w zakresie 1–3600 (domyślnie 300) i MaximumExecutions 1–100 (domyślnie 10). Kończy się po limicie czasu, liczby wykonań albo zamknięciu. Failed, blocked lub cancelled oznacza niepomyślny kod procesu. Retry nadal wymaga osobnego polecenia.

## Predykcje i ocena

Prematch korzysta z FootballResultDatasetRequest oraz wersjonowanej BacktestDefinition BS-012. Cutoff datasetu i targetów pochodzi z rzeczywistego czasu pobrania zadania z PostgreSQL. Publikacja używa niezmiennego ledgeru BS-011. Zachowane są hashe datasetu, cech i modelu oraz polityki źródła. Planowany termin, rzeczywiste wykonanie i cutoff cech są rozróżniane. Spóźnienie nie oznacza wykonania predykcji w przeszłości.

Stare definicje zachowują wymóg cutoff przed początkiem dnia meczu. Pipeline jawnie wybiera opcjonalną wersjonowaną politykę TargetTimePolicy="source-bound-kickoff-v1". Pozwala ona na target tego samego dnia tylko przy jednym źródłowym kickoff z precyzją minuty/sekundy, znanym i zapisanym przed cutoff, z datą UTC zgodną z targetem. Domyślny horyzont 12 godzin obsługuje więc także taki przypadek. Historia modelu nadal wyklucza wyniki z bieżącego dnia i przyszłości. Nieprecyzyjny lub sprzeczny kickoff, brak historii oraz odmowa uprawnień blokują publikację. Publikacja zamrożona po kickoff nie może udawać operacyjnej predykcji prematch. Nie zastępuj braków fikcyjnymi danymi ani późniejszymi przeglądami.

Postmatch wymaga PredictionExecutionId ukończonego pipeline oraz dokładnych BacktestId/BacktestHash. Zaplanuj go po uzyskaniu artefaktu prematch. Predykcja musi być rzeczywiście zamrożona przed potwierdzonym kickoff. Ocena odtwarza zapisane wartości i provenance modelu, bez ponownego liczenia modelu. Kontrakt v3 wymaga niezależnego kompletnego pokrycia wyników, źródłowego końca zdarzenia, precyzyjnych czasów i aktualnych praw. Braki dają wykluczenia i niedostępne metryki; pipeline nie certyfikuje kompletności dostawcy ani skuteczności operacyjnej.

## Odzyskiwanie

```powershell
$execution = '<UUID wykonania z inspect/status>'
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=cancel --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=retry --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=recover --Pipeline:ExecutionId=$execution @audit
```

Cancel jest trwały i kooperacyjny; nie usuwa już opublikowanych artefaktów. Retry dotyczy failed/blocked/cancelled bez opublikowanego wyniku pipeline. Recover wymaga wygasłego lease stanu running oraz braku aktywnej sesji właściciela. Sam upływ lease nie pozwala przejąć działającego workera. Oba polecenia zużywają ograniczony budżet prób i zachowują fingerprint żądania oraz planowany termin. Definicji działającego zadania nie można zmienić.

Przywrócenie pliku nie zatwierdza jego tożsamości. Każda próba ponownie sprawdza dokładne bajty, bieżące prawa, retencję, capability, budżet i wymagane przeglądy BS-013. Jeśli predykcje opublikowano przed awarią, odzyskanie używa istniejącego zamrożonego artefaktu. Niedokończona publikacja predykcji daje new_job_required_for_unpublished_prediction: zaplanuj nowe zadanie z aktualnym cutoff zamiast udawać wcześniejsze wykonanie. Istniejącego wyniku pipeline nie można nadpisać przez retry.

Przy zamykaniu worker próbuje zapisać cancelled w ograniczonym czasie. Awaria bazy lub utrata fencing pozostawia trwałe running do ręcznego odzyskania. Brak końcowego receipt nie jest sukcesem. Zawsze sprawdź również ledger operacji podrzędnych.

## Obserwacja

Status rozróżnia dostępność infrastruktury od ProviderDataReadiness=NotCertified. Sprawdź stan, lease, właściciela, liczbę prób, ostatni sukces, czasy wykonania i kategorie receipt. CorrelationId jest UUID wykonania. Logi strukturalne nie zawierają RAW, ścieżek pliku, sekretów ani treści dostawcy. Retencję zewnętrznego collectora konsoli skonfiguruj osobno.

Tabela pipeline.Diagnostics jest zarządzana migracją SQL: najwyżej 1000 zanonimizowanych wpisów operacyjnych, z usuwaniem wpisów starszych niż 30 dni przy każdym zakończeniu. Bezczynna baza nie wykonuje automatycznego czyszczenia. Niezmienne definicje, receipts i artefakty są osobnym audytem provenance. GET API oraz ograniczenia dashboardu Development pozostają bez zmian.
