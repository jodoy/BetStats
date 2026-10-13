# BS-017 — pozyskiwanie danych i dowody prospektywne

Wymagane: .NET 10, ręcznie zmigrowany PostgreSQL, dozwolony katalog RAW oraz
`DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats`,
`Ingestion__RawStoragePath`. Sekrety ustawiaj poza repozytorium i raportami.
Najpierw wykonaj [checklistę uprawnień źródła](../en/data/football-source-authorization.md).
Wewnętrzne zatwierdzenie polityki ani obecność pliku nie oznaczają praw dostawcy.

Pełne, wykonywalne polecenia PowerShell 7 i formaty JSON znajdują się w
[instrukcji angielskiej](authorized-acquisition.en.md).

1. Zweryfikuj źródło, prawa pobierania, przechowywania RAW, retencji i analizy.
   Monitoring wymaga także praw wyświetlania. Wymagania atrybucji, których workflow
   nie spełnia, blokują operację; ograniczona retencja wymaga wspieranego planu.
2. Wywołaj `Pipeline:Action=acquisition-plan` ze SourceId, PayloadPath, Competition,
   Season, Category i EvidenceKind. CSV ma istniejący profil football-history-v1,
   maksymalnie 1 MiB i 500 wierszy raportu. Plan przypina SHA256 i wersję polityki.
3. Z wyniku utwórz definicję FootballAcquisition; jawnie wywołaj plan, enable,
   run-once. Dispatcher wybiera globalnie jedno wymagalne zadanie: sprawdź JobId.
4. Sprawdź inspect z JobId, następnie acquisition-inspect/acquisition-readiness
   z ExecutionId. Partial zachowuje RAW i braki tożsamości. Użyj istniejącego
   przeglądu i FootballHistory:reconcile. Completed oznacza zakończenie kontroli,
   nie potwierdzoną kompletność źródła ani kwalifikację do prognozy.
5. Dla kickoffu dostarcz oryginalny plik EventTimeSourceClaim v1 z OriginalRawId,
   dokładnym ProviderEventReference, zakresem i czasem/strefą/precyzją źródła.
   W acquisition-plan wybierz KickoffEvidence i DateObservationId. Nie zgaduj UTC
   z kolumny Time. Nieznany czas publikacji pozostaje null. Korekta wymaga jawnego
   CorrectsKickoffId i tworzy nowe dowody.
6. Predykcje uruchamiaj istniejącym PrematchPrediction BS-015. Daty importu nie
   tworzą wiedzy historycznej. Oryginalna rozgrzewka, historia, kickoff i zakres
   pozostają obowiązujące. Zamrożony prior nie liczy się jako kwalifikująca prognoza.
7. Wyniki pobieraj jako PostmatchResults. Uzgodnienie, kompletność wyników i końce
   zdarzeń muszą spełniać dotychczasowe kontrakty. PostmatchEvaluation i walidacja
   BS-016 zachowują zamrożone predykcje, wykluczenia, liczebności i brak metryk.
8. Jawnie zaplanuj ProspectiveCohort z listą AcquisitionExecutions, opcjonalnymi
   ValidationExecutions i CoverageQueries. Odczyt: prospective-report z ExecutionId.
   Maksymalnie 10 akwizycji, 3 walidacje, 2 dokładne zapytania o pokrycie.

Synthetic oznacza fikcyjny test; OperatorDeclaredReal — deklarację operatora;
Unknown — nieznane pochodzenie. Osobno raportowany jest import historyczny i
obserwacja przed potwierdzonym kickoffem. Brak niezależnej certyfikacji źródła
i rzeczywistej kompletności jest jawny. Sukces parsowania nie jest dowodem pokrycia.
Ręczna deklaracja kompletności nie przejdzie mocnego przeglądu. Istniejące kontrakty
pełnych inwentarzy są wyłącznie syntetyczne.

Raport kohorty zachowuje oryginalne liczebności celów i modeli; liczba kwalifikujących
próbek oceny nie jest liczbą unikalnych meczów ani procentem skuteczności. Brak
kalibracji, małe próbki i braki danych nie są zamieniane w wyniki. Nie ma rankingu
modeli na nieporównywalnych zbiorach. Raporty są zamrożone; nowe obserwacje wymagają
nowego jawnego raportu. Cofnięcie praw, uszkodzenie RAW i wygaśnięcie pokrycia
blokują także stare odczyty.

Monitoring PL/EN: `/monitor/acquisition/<ExecutionId>?lang=pl-PL`,
`/monitor/acquisition-status/<JobId>?lang=en-GB`,
`/monitor/prospective/<ExecutionId>?lang=pl-PL`. API jest tylko do odczytu,
Development/loopback, bez wywołań dostawców, importu, modeli i oceny na GET.
Status zwraca do 100 wpisów. Retry/recover/cancel wykorzystują
[BS-015](automated-pipeline.pl.md); zachowaj zatwierdzony plik dla recovery.

Bez plików i praw rzeczywistego źródła można wykonać tylko oznaczone testy
syntetyczne. Transport HTTP pozostaje wyłączony, bez scrapingu i domniemanych praw
Footiqo/API-Football/Sofascore/Flashscore. BS-018 powinien zatwierdzić jeden konkretny
kontrakt dostawcy, konfigurację, schematy i niezależne dowody as-of/inwentarza.
