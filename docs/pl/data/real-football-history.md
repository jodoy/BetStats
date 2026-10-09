# BS-013: autoryzowany lokalny import historii piłkarskiej

Uruchamiaj komendy tylko w Development z jawną konfiguracją bazy i katalogu RAW.
Migracje są osobną czynnością operatora. Przed odczytem pliku wymagana jest
udokumentowana zgoda źródła: DataRetrieval, RawPayloadStorage,
HistoricalRetention i InternalAnalytics. Brak, niejednoznaczność, cofnięcie zgody
lub niespełnione ograniczenie oznacza odmowę. Wyświetlanie publiczne, uczenie,
redystrybucja i zastosowania komercyjne wymagają osobnych uprawnień. Ograniczona
retencja bez obsługiwanego planu retencji nie jest traktowana jako bezterminowa.
Repozytorium nie zawiera zgody dostawcy ani rzeczywistego pliku. Testy korzystają
wyłącznie z fikcyjnych danych projektu. Nie autoryzuj rzeczywistych danych przez
pomocniczy mechanizm demonstracyjny.

Profil `football-history-v1`: UTF-8 CSV, do 1 MiB i 5000 wierszy. Wymagane kolumny:
Div, Date, HomeTeam, AwayTeam, FTHG, FTAG. Opcjonalne: HTHG, HTAG, FTR, HTR,
Time, MatchId, Status, ResultBasis, PublishedAtUtc. Inne lub powtórzone nagłówki
są odrzucane. Profil nie akceptuje dowolnych eksportów dostawcy. Oryginalne bajty
zostają w RAW; przygotowanie osobnej projekcji także wymaga uprawnień.

Data ma format dd/MM/yyyy. Liga i sezon są jawnym kontekstem operatora. Aliasy
drużyn, sezon i mecz wymagają zatwierdzonych mapowań. Dwa identyfikatory tego
samego spotkania są konfliktem; sprzeczne wyniki nie zastępują się po cichu.
Brak MatchId oznacza złożony identyfikator z daty i drużyn, wymagający przeglądu.
Puste/unknown/unavailable wyniki pozostają null. Bez Status znana para goli oznacza
Finished, w przeciwnym razie Scheduled. Profil definiuje FTHG/FTAG jako wynik
regulaminowy; dla Finished domyślnie RegulationTime. Inny zakres musi być jawnie
opisany jako IncludesExtraTime, PenaltyShootout lub Unknown. Postponed, Cancelled,
Abandoned i dogrywka nie dają etykiet końcowego wyniku regulaminowego.

PublishedAtUtc jest opcjonalnym czasem UTC yyyy-MM-ddTHH:mm:ss.ffffffZ; brak
pozostaje null. Czasy pliku nie zastępują czasu publikacji ani historycznego zapisu.
Time jest walidowany, lecz nie stanowi zgadywanego czasu rozpoczęcia UTC.
Uszkodzone wiersze są raportowane, a niedomknięty plik zachowuje dowód RAW.

Ustaw `DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats` oraz
`Ingestion__RawStoragePath`. Każda komenda wymaga `FootballHistory:OperatorId`
i `FootballHistory:Reason`.

1. `FootballHistory:Action plan` z SourceId, PayloadPath, Competition i Season
   odczytuje autoryzowany plik bez parsowania i zwraca plan z SHA-256 i fingerprintem.
2. `capture` przyjmuje zwrócony obiekt Result jako PlanJson, PayloadPath,
   OperationId oraz `Approve true`. Zmieniony plik wymaga nowego planu i operacji.
3. Sprawdź tożsamości przez istniejące komendy Quality list/inspect/candidates/review.
   Mecz musi mieć jawnie zatwierdzone mapowanie na zamierzony obiekt kanoniczny.
4. `reconcile` z RawId, Competition, Season i Approve publikuje zatrzymany RAW po
   przeglądzie. RawId odczytaj z manifestu bazy dla run/AttemptId operacji.
5. `inspect` przyjmuje OperationId. `recover` przyjmuje te same dane co capture;
   wymaga zgodnego fingerprintu i zakończonej albo wygasłej dzierżawy. Blokada sesji
   źródła uniemożliwia przejęcie aktywnego importu. Poprawny powtórny import nie
   tworzy kolejnej wersji; korekta dopisuje nowy dowód i odnośnik do poprzedniego.
6. `recover-storage` z Approve kończy wyłącznie autoryzowane, zweryfikowane staging.
   Osierocone pliki wymagają osobnej autoryzacji obsługi.
7. `readiness` z SourceId pokazuje ligi/sezony, próbki i wykluczenia. Nie potwierdza
   kompletności metadanych ani wyników; brakujące mecze pozostają nieznane bez
   osobnego zatwierdzonego inwentarza. Zero certyfikowanych okien nie oznacza
   braku wszystkich danych, tylko brak potwierdzenia wymagań BS-011.

Raport Quality dla AttemptId pokazuje błędy wierszy. Reconciliation zgłasza
uszkodzenie RAW i niedostępny dowód. Historyczne wyniki zaimportowane dzisiaj nie
stanowią dowodu znanego przed meczem i nie uzasadniają deklaracji historycznej
trafności operacyjnej. Kontrakty modeli i ewaluacji pozostają obowiązujące.

API ma wyłącznie kontrakt transportu i ograniczonej paginacji. Nie ma aktywnego
HTTP, kluczy, scrapingu, harmonogramu ani importu przy starcie. Przyszły transport
musi egzekwować aktualną politykę na każdej stronie/próbie, timeout, limity i 429.
Identyfikator operatora jest wpisem audytu, nie uwierzytelnieniem.

Pełna składnia i szczegóły profilu: [English runbook](../../en/data/real-football-history.md).
