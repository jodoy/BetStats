# Jakość danych, przegląd tożsamości i reconciliation (BS-006)

[ADR 0019](../../adr/0019-quality-review-reconciliation.md),
[pełny kontrakt EN](../../en/data/quality-identity-reconciliation.md).
BS-006 rozwija scalone BS-005. Nie dodaje pobierania danych, uwierzytelniania,
publicznego API administracyjnego, dashboardu ani modeli predykcyjnych.

## Reguły i historia

Katalog `football.*` ma stałe identyfikatory i wersję 1. Sprawdza sport,
competition/season, datę i dostępne granice sezonu, różnych uczestników i ich
sport/typ, brakujące tożsamości, kolizje eventu, home/away, przejścia statusu,
sprzeczne daty, łańcuch korekt, proweniencję, starszy replay i rozbieżności źródeł.
Parser klasyfikuje duplikaty równoważne i sprzeczne. Późniejsza spójna zmiana daty
jest HistoricalCorrection; sprzeczne równoczesne dane pozostają konfliktem.
Różnica między providerami nie dowodzi, że któryś z nich jest błędny.

Severity Info/Warning/Error/Critical jest niezależne od eligibility. Poprawny
składniowo rekord bez mapowania ma Warning i blokuje analizę. Brak dat granicznych
sezonu nie prowadzi do wymyślenia przedziału. Completed/Cancelled są terminalne
w początkowym grafie statusów; inne korekty wymagają świadomie zmienionej reguły.

Nowa migracja dodaje wyłącznie `quality.QualityAssessments`, `quality.MaintenanceEvents`,
indeksy, ograniczenia i triggery. Oceny i audyt są append-only, z czasem INSERT
nadpisywanym przez PostgreSQL. UPDATE/DELETE/TRUNCATE oraz mutacje EF są blokowane.
Wcześniejsze migracje i historia pozostają zachowane. Administrator właściciel
może obejść zabezpieczenia; INSERT time nie oznacza COMMIT time.

## Jawny workflow lokalnego operatora

Użyj jednorazowej bazy i prywatnego katalogu RAW z BS-005. Konfiguracja przez
`ConnectionStrings__BetStats` i `Ingestion__RawStoragePath`; bez credentials w Git.

```powershell
dotnet tool restore
dotnet ef database update --project src/BetStats.Infrastructure
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
dotnet run --project src/BetStats.Worker -- --Quality:Action list --Quality:SourceId <source-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action inspect --Quality:IdentityId <identity-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action candidates --Quality:IdentityId <identity-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action review --Quality:SourceId <source-uuid> --Quality:IdentityId <identity-uuid> --Quality:Decision Approve --Quality:TargetKind Participant --Quality:TargetId <target-uuid> --Quality:ExpectedVersion 1 --Quality:OperatorId operator:fictional --Quality:Reason "Reviewed fictional fixture"
dotnet run --project src/BetStats.Worker -- --Quality:Action reconcile --Quality:RawId <raw-uuid> --Quality:Competition FICT --Quality:Season 2026-fiction --Quality:OperatorId operator:fictional --Quality:Reason "Reprocess reviewed identity"
dotnet run --project src/BetStats.Worker -- --Quality:Action report --Quality:ExecutionId <execution-uuid>
```

Zastąp UUID rzeczywistymi wynikami inspekcji i użyj aktualnej ExpectedVersion.
Identyfikator operatora jest deklaracją audytową, **nie uwierzytelnioną tożsamością**.
Polecenia przeznaczone są dla zaufanego lokalnego operatora. Nie ma endpointów HTTP.
Kandydaci są tylko listą tego samego sportu, nie automatycznym dopasowaniem po nazwie.
Approve sprawdza istniejący target, kind, źródło, sport i wersję decyzji. Publikacja
dodatkowo sprawdza kontekst sezonu i role. Nieznany kontekst sportu odmawia.

Reject/Ambiguous pomijają TargetKind/TargetId i dopisują Unresolved/Ambiguous.
Zmiana mapowania to kolejny Approve z aktualną wersją i uzasadnieniem. Jeden z dwóch
równoczesnych przeglądów wygrywa, drugi dostaje concurrent_review_conflict.
Wszystkie decyzje zachowują poprzednią referencję i audyt; nie ma fuzzy auto-matching.

Reconciliation ponownie sprawdza źródło, politykę oraz hash/rozmiar RAW. Nie pobiera
danych i nie tworzy nowego capture. Zachowuje receipty, poprzednie próby, obserwacje
i decyzje. Klucz zawiera RAW/hash, parser/rules version, scope i stan przeglądów.
Przetworzenie części pliku nie tworzy receipt całego payloadu. Starszy RAW nie cofa
nowszej korekty, także w uprzednio nierozpoznanym strumieniu. Zmiana mapowania nie
przenosi po cichu dawnych obserwacji na nowy event.

Wyniki: AlreadyProcessed, NewlyResolved, StillUnresolved, Conflict, Rejected, Failed.
Audyt ma Started i wynik końcowy, a błędy manifestu/storage własną referencję RAW.
Przerwanie zachowuje już zatwierdzone części. Jeśli DB uniemożliwi zapis końcowego
audytu, Started pozostaje widoczne. Po potwierdzeniu zatrzymania właściciela:

```powershell
dotnet run --project src/BetStats.Worker -- --Quality:Action interrupt --Quality:ExecutionId <execution-uuid> --Quality:OperatorId operator:fictional --Quality:Reason "Owner confirmed stopped"
```

## Dopuszczenie do analiz i raporty

HistoricalAsKnown używa obserwacji, RAW, decyzji, ocen i polityk znanych w T,
włącznie z zaufanym czasem zapisu. RetrospectiveReconstruction wymaga oddzielnego
ReconstructionAtUtc; może interpretować dawny fakt z później przejrzanym mapowaniem,
ale zwraca target oryginalny i interpretowany oraz referencje dowodów. Nie cofa czasu
nowej obserwacji i nie jest historyczną odpowiedzią bez tego oznaczenia.

```powershell
dotnet run --project src/BetStats.Worker -- --Quality:Action eligibility --Quality:ObservationId <observation-uuid> --Quality:AsOfUtc <utc-cutoff> --Quality:Purpose InternalAnalytics --Quality:Mode HistoricalAsKnown
```

Dla rekonstrukcji użyj Mode RetrospectiveReconstruction oraz późniejszego
`--Quality:ReconstructionAtUtc <utc-time>`. Oba tryby sprawdzają także bieżące
prawa i włączenie źródła, InternalAnalytics, wymagany cel użycia i HistoricalRetention.
Prawa pobierania/storage nie zastępują praw do analiz. Brak pełnych ocen, nieobsługiwana
wersja, sprzeczności, superseded facts, brak proweniencji i przekroczona retencja
odmawiają. Starsze dane nie dostają automatycznie dopisanej historycznej jakości.

Raport: Total = Accepted + Unresolved + Rejected; Rejected = Invalid + Duplicates +
Conflicts. Valid = Accepted + Unresolved, po wyłączeniu błędów, duplikatów i konfliktów.
Rates: Valid/Total, Accepted/Valid, Conflicts/Total, Eligible/Total. Dla pustego
raportu zero. Eligibility jest dodatkową oceną bieżącą i nie zmienia dawnych ocen.
Limit raportu 5000 rekordów; overflow odmawia zamiast ukrywać część licznika.
Gdy wadliwy nagłówek pozwala policzyć wiersze, każdy jest odrzucony. Payload bez
wiarygodnej liczby wierszy trafia do osobnego PayloadFailures, nie do mianownika.
Batch Application: maksymalnie 20 różnych RAW; Worker obsługuje jeden RAW.
Listy i inspekcje są ograniczone i stabilnie sortowane; pełne limity w kontrakcie EN.

Testy używają fikcyjnych danych, PostgreSQL 17 Testcontainers i izolowanych katalogów.
Sprawdzają A–G, hash, prawa, korekty, historyczne cut-offy, konkurencję, raporty,
ochronę SQL/EF i upgrade z BS-005. Nie pomijamy testów PostgreSQL.
Reset ręczny: nowa jednorazowa baza i katalog jak w BS-005, nigdy wyłączanie triggerów.

## Ograniczenia i BS-007

Brak uwierzytelniania operatora, produkcyjnego storage, distributed leases,
materializowanych datasetów i gwarancji snapshotu COMMIT. Prawa do realnego providera
nadal wymagają niezależnej weryfikacji. Nie ma automatycznego purge/retention bypass.
Początkowy pełny katalog dotyczy metadanych eventów piłkarskich; inne kategorie
bez wystarczających dowodów odmawiają eligibility.
Rekomendowany BS-007: kontrakty składania datasetów i manifesty dowodów z jawnymi
trybami historycznym/rekonstrukcyjnym albo osobno chroniony interfejs operatora.
