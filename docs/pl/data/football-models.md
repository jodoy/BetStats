# Modele piłkarskie v1 (BS-012)

Modele matematyczne i symulacje historyczne wyłącznie dla Development. Bez
rzeczywistych dostawców, kursów, uczenia ML, produkcyjnego scoringu/API/UI ani
deklaracji skuteczności. Bazą jest scalony BS-011 PR #15, main `352d920c`.
Pełne parametry i wzory: [runbook EN](../../en/data/football-models.md).
Decyzja: [ADR 0026](../../adr/0026-football-prediction-models.md).

Definicja backtestu v2 zawiera niezmienną definicję Model v1. Dostępne predyktory:
`football-elo`, `football-poisson`, `football-dixon-coles` v1. Zachowane są bajty
starszych definicji, datasetów v1/v2/v3 i backtestów BS-011. Nie ma nowej migracji;
pozostaje 12 migracji BS-011 i istniejący rejestr operacji z blokadą właściciela.

Historia obejmuje tylko kanoniczne, rozstrzygnięte wyniki czasu regulaminowego
z poprzednich dni UTC w oknie 30 dni, znane na obu zegarach przed T. Późniejsze
korekty ani przeglądy nie zmieniają zamrożonych wejść. Każde odcięcie walk-forward
odtwarza własny stan; bez losowego podziału, uczenia parametrów czy przyszłego stanu.
Elo aktualizuje mecze jednego dnia partiami z ratingów sprzed tego dnia. Konfigurowalne
są rating bazowy, przewaga domu, K i zachowanie na granicy sezonu. To rating z
ograniczonego okna/scope datasetu, nie historia całej kariery.

Poisson publikuje jedną wspólną tabelę dokładnych wyników, z której wynikają 1X2,
BTTS, O/U2.5, suma goli i oczekiwana liczba goli. Odrzucona masa ogona jest jawna;
przekroczenie tolerancji powoduje błąd, bez obcinania intensywności. Dixon-Coles
wymaga dodatnich czterech współczynników niskich wyników i jawnego rho. Parametry
są deklarowane, nie wyuczone. Pierwsza połowa ma własną historię i prior 0.55;
nie dzielimy wyniku pełnego meczu przez dwa.

Rozgrzewanie wymaga domyślnie 3 obserwowanych meczów każdej drużyny oraz osobno
3 uzasadnionych wyników pierwszej połowy. Przy brakach powstaje jawna symulacja
prior, wykluczona z odpowiednich metryk. Brak dowodu nie oznacza zera ani kompletności.
Opcjonalny ensemble pozostaje wyłączony do czasu uzyskania zweryfikowanego artefaktu
kalibracji; nie nazywamy niekalibrowanych priorów skalibrowanymi prognozami.

Ewaluacja nadal wymaga wszystkich bramek BS-011: aktualnych uprawnień, poprawnego
RAW, niezależnej kompletności wyników, precyzyjnego i uzasadnionego końca wydarzenia,
horyzontu i zegarów wyniku. Porównania używają przecięcia kwalifikujących się wydarzeń
dla każdego rynku, tego samego datasetu, odcięcia, etykiet i definicji metryk.
Raport pokazuje liczności, wykluczenia, Accuracy/Brier/Log Loss/MAE i kalibrację.
Puste próby mają null, próby poniżej 100 nie spełniają progu walidacji.

## Polecenia operatora

Zbuduj Release, ustaw `DOTNET_ENVIRONMENT=Development`, lokalne połączenie i ścieżkę
RAW. Hosty nie migrują, nie importują i nie uruchamiają backtestów automatycznie.
Nie zapisuj sekretów ani RAW w repozytorium. Każde polecenie wymaga
`Model:OperatorId` i `Model:Reason`; nazwa operatora jest deklaracją, nie logowaniem.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Model__OperatorId = 'operator:local'
$env:Model__Reason = 'Explicit mathematical inspection'
$env:Model__Action = 'inspect'
$env:Model__DefinitionJson = '{"Version":1,"Kind":"Poisson","Elo":{},"Goals":{},"MinimumMatches":3,"MinimumHalfMatches":3,"HistoryDays":30}'
dotnet run --project src/BetStats.Worker --configuration Release --no-build
```

- `inspect`: definicja/hash/wymagania; `simulate`: dodatkowo InputJson v2 z jawnymi
  zegarami i historią. To niezweryfikowana symulacja matematyczna bez zapisu do DB.
- `plan`/`backtest`: pełny BacktestDefinition v2 w DefinitionJson, z rzeczywistymi
  identyfikatorami/hashami zweryfikowanego datasetu v3 i Model. `backtest` wymaga
  OperationId oraz Approve=true. Schemat metryk jest taki sam jak w BS-011.
- `operation`: OperationId; `report`/`verify`/`verify-deep`: SnapshotId;
  `recover`: OperationId, ExpectedFingerprint, Approve=true;
  `compare`: SnapshotIds rozdzielone przecinkami (2..10), pełna weryfikacja i prawa.

Zapis i odzyskiwanie korzystają z BS-011: aktualne prawa sprawdzane przy publikacji,
idempotencja, fingerprint, zegar DB, lease, token właściciela. Alias backtest/report
zachowuje w delegowanym JSON nazwy run/inspect. Artefakt ma limit 16 MiB, żądanie
1 MiB; zbyt duża siatka/liczba wydarzeń powoduje błąd bez publikacji. Nie wolno
zmniejszać siatki, jeśli naruszy to tolerancję masy ogona.

Nowo zapisane fikcyjne dane bez historycznych dowodów dają **0 kwalifikujących się
próbek operacyjnych**. Dodatnie testy matematyczne są jawną fikcją z kontrolowanym
zegarem; nie cofamy dat zapisu w PostgreSQL. Wyniki weryfikacji:
[raport BS-012](../../en/quality/bs-012-verification.md).
