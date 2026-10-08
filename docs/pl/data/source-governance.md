# SourcePolicy i governance danych (BS-004)

Decyzje: [ADR 0016](../../adr/0016-source-governance-and-bounded-history.md).
Pełne kontrakty: [dokumentacja EN](../../en/data/source-governance.md).
Nie dodano licencji, rzeczywistych danych, poświadczeń ani klientów providerów.

## Cztery niezależne kwestie

1. Dostęp techniczny: czy plik/API da się pobrać?
2. Zgoda wewnętrzna: czy BetStats sprawdził dowody i zatwierdził planowany cel?
3. Prawo umowne/licencyjne: czy uprawniony podmiot rzeczywiście zezwala na ten cel?
4. Prawa do wyników: czy dane i wyniki pochodne wolno wyświetlać, trenować,
   redystrybuować lub komercjalizować?

Approved oznacza wewnętrzną ocenę, nie zgodę udzieloną przez providera ani opinię
prawną. Allowed zapisuje wniosek recenzenta oparty na dowodach. Przed ingestion
trzeba osobno zweryfikować rzeczywiste warunki i prawa do wyników. Unknown odmawia.
W rekordach przechowujemy wyłącznie odwołania do warunków/dowodów, bez dokumentów
objętych prawami autorskimi, kluczy, haseł, podpisanych URL ani prywatnej korespondencji.

## Model i audyt

Schemat governance zawiera:

| Tabela | Zawartość |
| --- | --- |
| SourcePolicies | UUID, źródło, wersja, przedział UTC, TermsReference, EvidenceReference, CreatedAtUtc i czas zapisu DB |
| PurposePermissions | Cel, Allowed/Denied/Unknown, opcjonalna atrybucja i limit dni retencji |
| PolicyAudits | Sekwencja zatwierdzenia/cofnięcia, poprzednik, status, recenzent, powód, czasy przeglądu/zatwierdzenia i zaufany czas DB |

Dziewięć niezależnych celów: MetadataDiscovery, DataRetrieval, RawPayloadStorage,
HistoricalRetention, InternalAnalytics, PublicDisplay, ModelTraining, CommercialUse,
Redistribution. Pominięte definicje otrzymują Unknown. Zatwierdzenie wymaga dziewięciu
rekordów, ale nie zmienia ich uprawnień. Najpierw zapisujemy definicję Draft,
następnie dopisujemy decyzję Approved. Revoked jest kolejną decyzją, nie edycją
zatwierdzenia. Cofniętej wersji nie reaktywujemy: tworzymy nową wersję.
SourcePolicy wylicza Status i metadane review z załadowanej kolekcji Audit;
niepełny aggregate nie stanowi autorytatywnego zatwierdzenia.

Unikalność źródło/wersja oraz polityka/sekwencja chroni współbieżność. FK RESTRICT
nie usuwa historii przez cascade. EF i SQL triggery blokują zmiany/usuwanie historii;
po audycie nie można dopisywać uprawnień. Zatwierdzenie blokuje źródło i odrzuca
nakładające się przedziały aktywnych zatwierdzeń [od,do). Przed nowym zatwierdzeniem
nakładającej się wersji trzeba cofnąć poprzednią. Sąsiadujące przedziały są dozwolone.
Zapisy audytu wymagają READ COMMITTED albo SERIALIZABLE; inne izolacje są odrzucane.

## Ocena i kontrakty providerów

ISourcePolicyEvaluator otrzymuje źródło, cel, czas UTC, UsageContext i cancellation.
Zwraca wynik, reason code, politykę/wersję, czas i ograniczenia. Brak polityki,
Draft, Revoked, nieaktywny przedział, Unknown, Denied i konflikt Approved oznaczają
odmowę. Kontekst dodatkowo wymaga osobnych praw display/commercial/training/
redistribution. Deklarowana atrybucja i retencja muszą spełniać ograniczenia;
przyszły kod musi faktycznie je realizować i audytować. Wynik nie jest stałym tokenem
autoryzacji: ponownie sprawdzaj politykę przed każdą operacją. Audyt wykonań i
koordynacja cofnięcia praw w długich zadaniach pozostają planowane.

Application zawiera IProviderAdapter, opis źródła/sportów/capabilities, walidację
konfiguracji, cancellable wykonanie i kategorie błędów. MetadataDiscovery wymaga
tego celu; EventMetadata/HistoricalObservations wymagają DataRetrieval.
AuthorizedProviderExecutor sprawdza lokalną konfigurację i capabilities, politykę,
bieżący status źródła przed polityką i ponownie przed budżetem, a dopiero potem
adapter. Brak/wyłączenie źródła odmawia niezależnie od praw historycznych (BS-004.1).
Walidacja konfiguracji nie może wywoływać API.
Brak klienta HTTP/SDK. Wynik jest statusem/błędem; najmniejszy typowany kontrakt
danych będzie należał do BS-005. Nie ma automatycznych retries, także dla auth.

Budżet wymaga dodatnich limitów minutowych, dobowych i concurrency oraz timeout
(0, 5 minut]. Zegar TimeProvider umożliwia testy bez czekania/HTTP. Limity są
przesuwanymi oknami 60 sekund i 24 godzin; cooldown obsługuje Retry-After jako
delay lub datę. Krótszy cooldown nie skraca dłuższego. Wywołanie przy wyczerpaniu
odmawia od razu. Używaj jednej wspólnej instancji budżetu na providera/proces,
nie nowej na każde żądanie. Restart zeruje liczniki; wiele instancji nie jest
koordynowanych. Nie dodano infrastruktury rozproszonej. Adapter ignorujący
cancellation utrzymuje lease concurrency aż zakończy działanie.

## Tożsamość i paginacja

DecidedAtUtc jest czasem zdarzenia podanym przez wywołującego. Nowy RecordedAtUtc
nadaje PostgreSQL i nadpisuje dowolną wartość INSERT. Historyczne zapytanie wymaga
obu czasów <= AsOfUtc. Stare dane otrzymują czas migracji, ponieważ ich pierwotnej
zaufanej dostępności nie da się odtworzyć. Wcześniejsze cutoffy konserwatywnie je
wykluczają; stare rekordy i DecidedAtUtc pozostają zachowane. Migracja może fizycznie
przepisać tabelę podczas backfill; trzeba ocenić czas/blokady przy większej bazie.
RecordedAtUtc oznacza INSERT, nie COMMIT. Późny commit może zmienić powtarzany
wynik historyczny. Ufamy zegarowi DB i zwykłym uprawnieniom; administrator może
wyłączyć triggery. BS-004.1 dodaje też zaufany czas zapisu obserwacji i walidację
relacji RAW: [naprawy audytu i retencja](audit-remediation.md).

ReadPageAsOfAsync używa klucza AvailableAtUtc/CreatedAtUtc/UUID. Limit domyślny
200, konfigurowalny 1–1000 przez History:MaximumPageSize albo zmienną
History__MaximumPageSize. Każda strona ponawia AvailableAtUtc/RecordedAtUtc <= AsOfUtc
i filtry; kursor wiąże cały
oryginalny query i sprawdza UTC/precyzję, UUID i cutoff. Pobieramy pageSize+1,
bez offsetów. Remisy czasu nie dublują ani nie gubią istniejących rekordów.
ReadAsOfAsync działa dla małych wyników, przy przekroczeniu limitu zgłasza błąd.
Kursory są obiektami Application, bez podpisanego formatu publicznego API.
Strony nie są snapshotem: nowa cofnięta obserwacja za kursorem może zostać pominięta,
a przed nim pojawić się na kolejnej stronie. Eksport stałego datasetu wymaga
przyszłego kontrolowanego snapshotu/izolacji.

## Migracja i następny krok

20261008005300_SourceGovernanceAndTrustedHistory dodaje trzy tabele, constraints,
indeksy i triggery oraz dostępność decyzji tożsamości. Nie zastępuje migracji
BS-002/003 ani nie kasuje ich historii.
Rollback usuwa nową historię governance oraz kolumnę zaufanej dostępności;
wymaga przeglądu SQL i kopii danych. Host nie uruchamia migracji automatycznie.
Testcontainers potwierdza świeżą bazę, upgrade BS-003, zachowanie danych,
autoryzację, audyt, współbieżność, dostępność
i paginację. Wszystkie stare zestawy testów pozostają w CI; CodeQL jest zachowane.

BS-005 powinno objąć jednego osobno zweryfikowanego providera/sport/capability:
konfiguracja, wspólny budżet, sprawdzanie praw przed operacją, dozwolony RAW,
typowana normalizacja, zaufana dostępność i testy kontraktu. Prawa do każdego celu
i wyniku pochodnego trzeba zweryfikować przed włączeniem. Klienci/provider downloads,
live ingestion, statystyki, fuzzy matching, predykcje, ML, UI, auth, billing
i produkcyjne wdrożenie pozostają poza zakresem.
