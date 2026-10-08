# Pokrycie historyczne, czas zdarzeń i kontrakty ewaluacji (BS-008)

[ADR 0021](../../adr/0021-coverage-time-evaluation.md) i [pełny opis angielski](../../en/data/coverage-time-evaluation.md)
opisują kontrakty, ograniczenia, migrację i komendy. Pokrycie dowodów nie jest dowodem
kompletności rzeczywistych rozgrywek. Nie ma modeli, wyników sportowych ani metryk jakości modeli.

Zakres obejmuje źródło, sport, rozgrywki, sezon, opcjonalnego uczestnika, typ zdarzenia,
typ obserwacji i półotwarty przedział `[początek,koniec)`. Przedziały dat i UTC są osobne;
daty bez czasu nie otrzymują fikcyjnego kickoffu. Raporty mają limit 200 twierdzeń,
stabilną kolejność, luki, konflikty, wygasłe dowody, brakujące typy, T/R i uprawnienia.
Brak danych nie oznacza zera zdarzeń; nie podajemy procentu bez znanego mianownika.

Unknown oznacza brak uzasadnionego zatwierdzonego pokrycia, Partial ograniczone dowody,
VerifiedComplete potwierdzoną kompletność dokładnego zakresu, VerifiedEmpty potwierdzony
pusty zakres, Conflicting sprzeczne dowody, Expired dowody wygasłe. Sama ingestia,
ciągłość dat czy liczba wierszy nie uzasadnia kompletności. Silne zatwierdzenie wymaga
przeglądu oraz kontraktu `project-owned-fixture-inventory-v1`: RAW zawiera dokładny
zakres i wyczerpujący spis obserwacji własnej fikcyjnej fixture. Ręczna deklaracja nie
wystarcza. Kontrola tożsamości i jakości nie może ukryć zdarzeń jako pustego zakresu.
Żaden rzeczywisty dostawca nie otrzymuje tu gwarancji kompletności.

Twierdzenia, decyzje i czasy mają historię append-only, RAW/SHA-256, politykę, wersję,
czas publikacji/pobrania/dostępności/zapisu DB i ważność. Triggery PostgreSQL chronią
UPDATE, DELETE, TRUNCATE oraz czas zapisu; EF chroni także mutacje śledzone. Właściciel
DB może ominąć zabezpieczenia, dlatego runtime wymaga ograniczonej roli (ADR 0017).
Późniejsza aprobata nie poprawia wcześniejszego HistoricalAsKnown. Jawny R w
RetrospectiveReconstruction umożliwia późniejszą interpretację; aktualne prawa,
retencja i ważność pozostają osobnymi bramkami użycia.

DateOnly, Minute, Second i Unknown są jawne. Brak strefy/offsetu, luka DST, dwuznaczny
DST bez zgodnego offsetu, sprzeczne UTC lub przepełnienie zwracają niepewność. Nie
wybieramy arbitralnie jednej godziny DST. Tylko jawna korekta wskazująca poprzedni dowód
go zastępuje; niezależne sprzeczne twierdzenia pozostają dostępne. Reguły stref pochodzą
z systemu; manifest utrwala użyte rozstrzygnięcie.

Cztery cechy BS-007 zachowują nazwy i semantykę częściowej historii obserwowanej.
Deklarują typy, okno, uczestnika, status Completed, jakość v1 i wymagane pokrycie.
Brak nie staje się zerem; sprzeczne, wygasłe lub niedozwolone pokrycie blokuje wartości.
Manifest/cechy/definicja v2 utrwalają dowody, decyzje, precyzję, bramki i niepewność.
Obejmuje to czasy celu i istotnych zdarzeń historycznych, najwyżej 200 twierdzeń na wiersz.
V1 zachowuje oryginalne bajty i hashe; nowe dowody tworzą nowy artefakt przy nowym
cutoffie lub jawnym R. Migracja `20261008134149_CoverageEventTimeEvaluation` dodaje trzy
tabele i sześć triggerów, bez zmiany opublikowanych migracji i historii BS-007.

Kontrakty przyszłej ewaluacji obejmują zwycięzcę, sumę goli, BTTS i gol w pierwszej
połowie. Etykieta po predykcji może służyć późniejszej ewaluacji, ale nie wcześniejszym
cechom. Są wymagane prawa, kompletność, jakość, czas i wersjonowanie korekt. Log loss,
Brier, accuracy, kalibracja (binarnie, 10 przedziałów) oraz MAE mają wersję, format,
zakres, co najmniej 100 próbek, reguły braków/wag/agregacji. Niczego nie obliczamy.

Komendy wymagają środowiska Development, jawnego OperatorId i Reason. Ta tożsamość nie
jest uwierzytelniona. Używaj wyłącznie osobnej lokalnej bazy i RAW poza repozytorium.
ConnectionStrings__BetStats i Ingestion__RawStoragePath nadpisują konfigurację przez
zmienne środowiskowe; nie zapisuj danych logowania do Git.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Worker --configuration Release --no-build -- --Coverage:Action=evaluation-contracts --Coverage:OperatorId=operator:local --Coverage:Reason="Inspect future contracts"
```

Akcje: inspect (EvidenceId), review (EvidenceId/ExpectedSequence/Decision/BasisReference),
report (QueryJson), time (IdentityId/QueryJson), feature-gate (QueriesJson/RequirementJson),
record i record-time (SubmissionJson z wcześniej zapisanym RAW). Review dopisuje trwałą
decyzję; odczyty wypisują aktora/powód i niczego nie zatwierdzają. Dataset:Action=build
przyjmuje DefinitionJson; dla v2 ustaw Version=2 i FeatureSchemaVersion=2. Kompletna
składnia, bezpieczna aprobata syntetyczna i ograniczone SELECT są w opisie angielskim.
Nie ma API administracyjnego, schedulera ani wywołań zewnętrznych dostawców.

Testy A–M obejmują 10 obserwacji bez gwarancji, dokładny zakres, historię zatwierdzeń,
konflikt/pusty zakres, DateOnly/DST/korekty, bramki cech, nowe snapshoty, późne etykiety
i cofnięcie praw. Testy PostgreSQL 17 wymagają Docker; awaria środowiska nie oznacza
pominięcia ani sukcesu. Dokładne wyniki lokalne i CI znajdują się w PR. Zalecany BS-009:
jeden dozwolony kontrakt kompletności/czasu i pochodzenie etykiet przed modelem lub
wykonywaniem backtestów.
