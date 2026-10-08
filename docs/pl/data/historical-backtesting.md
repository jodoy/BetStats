# Historyczny backtesting (BS-011)

Silnik służy do jawnych **symulacji historycznych w Development**, z syntetycznym
baseline'em o stałych parametrach. Nie jest modelem ML ani scoringiem produkcyjnym.
Bazą jest `main` po scaleniu PR #14 (BS-010), commit `0efd98f`. Task nie scala PR.
Szczegóły kontraktów: [runbook EN](../../en/data/historical-backtesting.md), ADR 0025.

Predykcja zapisuje tożsamość wydarzenia, cutoff T, wersję predyktora, ID/hash datasetu,
osobny hash cech/wejścia, prawdopodobieństwa lub oczekiwaną liczbę goli i referencje
dowodów. Czas zapisu PostgreSQL oznacza rzeczywiste wykonanie symulacji; nie jest
cofany do T. Dataset v3 może zawierać późniejsze etykiety, ale predyktor otrzymuje
wyłącznie kanoniczne cechy znane w T. Późniejsze decyzje, wyniki docelowego meczu,
korekty i reinterpretacja cech po T powodują odmowę. Artefakty v1/v2/v3 pozostają
niezmienione. Nowe raporty i historia operacji są append-only.

Etykiety są czytane osobno w cutoff ewaluacji, z jawnym T/R. Wymagane są jakość,
tożsamość, bieżące uprawnienia, kompletne pokrycie metadanych cech, niezależne
Complete pokrycie wyników i precyzyjny koniec wydarzenia związany z wynikiem oraz RAW.
Zmiana daty wyniku względem zamrożonego celu wyklucza próbkę; pokrycie dawnego dnia
nie uzasadnia kompletności dnia przełożonego meczu. Nie wyliczamy końca z kickoffu
ani stałego czasu trwania. Unknown/Partial/Empty/
Conflict/Expired nie oznaczają Complete. Brak pierwszej połowy pozostaje brakiem.
Wersja definicji >=3 ogranicza dopuszczalną wersję korekt wyników/końca wydarzenia.
Zmiana cutoffu lub definicji wymaga nowego raportu, bez zmiany dawnych predykcji.

Obsługiwane cele: 1X2, Over/Under 2.5, BTTS, wystąpienie gola w pierwszej połowie,
liczba goli regulaminowych i uzasadniona liczba goli pierwszej połowy. Baseline v1
ma jawne fikcyjne priory .4/.3/.3, .5/.5 oraz oczekiwane 2 lub 1 gola. Nie traktujemy
brakujących cech jako obserwowanego zera i nie estymujemy modelu z tych danych.

Metryki v1: Accuracy (remis prawdopodobieństw wybiera pierwszą klasę), Brier
(suma błędów kwadratowych wszystkich klas, także obu klas binarnych, zakres 0..2),
naturalny Log Loss (zero prawdopodobieństwa prawdziwej klasy daje jawne +infinity,
bez epsilon), MAE dla liczby goli i kalibracja w 10 równych przedziałach na klasę.
Logarytmy są zaokrąglane do 12 miejsc; prawdopodobieństwa decimal muszą sumować się
dokładnie do 1. P=1 trafia do ostatniego przedziału. Mianownik obejmuje tylko
kwalifikujące się etykietowane próbki, o jednakowej wadze. Pusty zbiór daje null,
nie zero. Raport zawiera requested/eligible/excluded, przyczyny i flagę minimum
100 próbek. Wyniki poniżej minimum są opisowe, bez twierdzeń o jakości modelu.

Świeżo zaimportowana fikstura nie ma historycznych zapisów sprzed meczu. Udane
wykonanie może zatem opublikować raport z 0 eligible oraz jawnymi brakami dowodów.
Dodatnie scenariusze historyczne są testami jednostkowymi z kontrolowanym zegarem;
nie są antydatowanymi obserwacjami PostgreSQL ani dowodami kompletności providera.

## Polecenia

Zbuduj Release. Jawnie migruj wybraną lokalną bazę zgodnie z runbookiem persistence.
Host nie migruje, nie importuje i nie wykonuje ewaluacji przy starcie. Ustaw lokalnie
`ConnectionStrings__BetStats`, `Ingestion__RawStoragePath` i `DOTNET_ENVIRONMENT=Development`.
Nie zapisuj credentials ani RAW w repo. Uruchamiaj aplikację rolą DB niebędącą ownerem.

Szablon JSON i pełne rekordy metryk są w runbooku EN; wstaw rzeczywiste ID/hash
zweryfikowanego v3 i SportId, aktualny cutoff UTC z sześcioma cyframi ułamkowymi.
Nie wykonuj szablonu z placeholderami. Ustaw:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Backtest__DefinitionJson = Get-Content -Raw ./local-backtest-definition.json
$env:Backtest__OperatorId = 'operator:local'
$env:Backtest__Reason = 'Przeglad dozwolonej fikcyjnej symulacji'
dotnet src/BetStats.Worker/bin/Release/net10.0/BetStats.Worker.dll --Backtest:Action=plan
```

`plan` nie zapisuje zmian. `run` wymaga jawnego `Backtest__OperationId` (nowy UUID)
i `Backtest__Approve=true`; `operation` odczytuje stan. `inspect`, `verify` i
`verify-deep` wymagają `Backtest__SnapshotId`. Deep sprawdza RAW oddzielnie od
integralności artefaktu. Bieżąca odmowa uprawnień blokuje ujawnianie dowodów i RAW.
Polecenie zakończone z wykluczeniami może mieć exit 0; to nie oznacza jakości modelu.
Nieudana weryfikacja i operacje Failed/Running/Cancelled mają exit 1.

`recover` wymaga UUID operacji, `Backtest__ExpectedFingerprint` z historii, actor,
reason i jawnej zgody. Odtwarza dokładny zapisany request. Nie przejmuje aktywnego
lease. Po wygaśnięciu/przerwaniu lub Failed/Cancelled nadaje nowy fencing token.
Succeeded zwraca stary artefakt po kontroli bieżących uprawnień. Zmiana requestu
pod tym samym UUID jest błędem. Publikacja i Succeeded są jedną transakcją; stare
procesy nie mogą publikować. Nie ma automatycznego retry/recovery ani heartbeat.
Lease domyślnie wynosi 10 minut; dłuższa operacja wymaga jawnego wznowienia.

Ograniczenia: kompletność tylko własnej fikcyjnej umowy inventory; nazwa operatora
jest deklaracją, nie uwierzytelnieniem. Owner DB może ominąć triggery. Usunięcie RAW
po publikacji ujawnia deep verify, bez zmiany historii. Nie zweryfikowano dodatniej
ewaluacji na rzeczywistych historycznych danych providera. Brak transportu HTTP,
kursów, treningu, produkcyjnego API/UI, deploymentu i automatycznego merge.
