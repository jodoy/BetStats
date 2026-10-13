# BS-016 — instrukcja operatora

Workflow jest dostępny wyłącznie w Development. Wymaga .NET 10, PostgreSQL,
ręcznie zastosowanych migracji, `DOTNET_ENVIRONMENT=Development`,
`ConnectionStrings__BetStats` oraz `Ingestion__RawStoragePath`. Nie zapisuj sekretów
w repozytorium ani raporcie. Actor/Reason są deklaracjami audytowymi operatora.

1. Uzyskaj plik i potwierdzone prawa źródła. Użyj importu BS-013, przeglądu
   tożsamości i uzgodnienia danych. Profil oraz zakres muszą odpowiadać RAW.
2. Przygotuj `validation-input.json` według [pełnego przykładu i komend](real-data-validation.en.md).
   Dopuszczalne profile: `football-history-v1`, `results-v1`, `metadata-v1`.
   Limit: jeden RAW do 1 MiB, 500 wierszy, 2000 problemów parsera, trzy pary publikacji.
3. Uruchom `Pipeline:Action=inspect-readiness` z `Pipeline:ValidationInputPath`.
   Kontrola jest jawna i nie zapisuje raportu ani nowej prognozy.
4. Utwórz zadanie `FootballValidation` poleceniem `plan`, włącz `enable` i wywołaj
   `run-once`. To globalny dispatcher: może wybrać inne włączone zadanie.
5. Sprawdź `inspect` z JobId. Użyj ExecutionId do `validation-report` i
   `verify-validation`. Kompletne polecenia PowerShell i format definicji znajdują
   się w instrukcji angielskiej. `Completed` oznacza zakończenie kontroli, a nie
   potwierdzoną gotowość danych. Odczytaj osobne Readiness, Issues i Limitations.

Klasyfikacja `Synthetic` oznacza test fikcyjny, `Unknown` — nieznane pochodzenie,
`OperatorDeclaredReal` — deklarację operatora, której system nie potwierdza
niezależnie. Brak rzeczywistego uprawnionego zbioru oznacza niedostępną walidację
rzeczywistych danych. Nie wyciągaj skuteczności modeli z testów syntetycznych.

Predykcje Elo/Poisson/Dixon-Coles wykonuj istniejącym zadaniem BS-015
`PrematchPrediction`. Dokładny kickoff oraz dowody as-of muszą istnieć przed
faktyczną granicą wiedzy. Import historii teraz nie tworzy dawnej obserwacji.
Po uzyskaniu kwalifikujących się wyników uruchom `PostmatchEvaluation` i wpisz
powiązane ExecutionId w Publications. Braki, korekty z przyszłości, niepełna
historia i rozgrzewka nie mogą zostać zamienione w kwalifikujące się próbki.
Metryki zachowują oryginalne liczebności, wykluczenia, null i brak kalibracji.

Raport zachowuje osobno datę meczu, publikację źródła, pobranie, dostępność,
zapis w bazie, planowaną/faktyczną granicę predykcji i czas wykonania/publikacji.
Daty CSV nie potwierdzają dokładnego kickoffu. Kontrola raportu nie dowodzi
odzyskiwania po awarii na danych operatora.

Przy aktualnych prawach wyświetlania otwórz `/validation/<ExecutionId>?lang=pl-PL`
w dashboardzie. Wersja angielska: `lang=en-GB`. Odczyt tylko na loopback,
bez uruchamiania importów, modeli lub oceny. Dane z wymaganiami atrybucji,
których workflow nie spełnia, są blokowane. Cofnięte prawa i uszkodzone RAW
blokują także wcześniej zapisany raport.

Anulowanie, retry, recover i ograniczony worker wykorzystują
[instrukcję BS-015](automated-pipeline.pl.md). Kolejne wywołanie odczytu nie
tworzy publikacji predykcji. Zachowaj oryginalną definicję i identyfikatory.
Eksport raportu musi respektować retencję; trwałe artefakty podlegają istniejącej
uprzywilejowanej procedurze, bez nowego obejścia zabezpieczeń.

Rekomendacja BS-017: zatwierdzony kontrakt pozyskania i archiwizacji rzeczywistych
danych, dowody as-of, niezależna kompletność wyników i prospektywna kohorta oceny.
