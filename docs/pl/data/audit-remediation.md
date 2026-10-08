# Naprawy audytu (BS-004.1)

Decyzje: [ADR 0017](../../adr/0017-audit-remediation-and-trusted-observations.md).
Pełne kontrakty i procedura retencji: [dokumentacja EN](../../en/data/audit-remediation.md).

## Dostępność obserwacji

PostgreSQL nadaje RecordedAtUtc podczas INSERT i nadpisuje wartość wywołującego.
EF ignoruje przekazany czas. Historia i każda strona wymagają jednocześnie
AvailableAtUtc <= AsOfUtc oraz RecordedAtUtc <= AsOfUtc. Kolejność pozostaje
AvailableAtUtc/CreatedAtUtc/UUID; korekty i utrwalone odwołania kanoniczne pozostają.
Czasy wydarzenia, publikacji, pobrania, utworzenia, deklarowanej dostępności oraz
zapisu DB są odrębne. Cofnięte daty nie otwierają wcześniejszego cutoffu.

Historyczny RAW można przetworzyć później. Obserwacja musi należeć do tego samego
źródła, pobranie nie może poprzedzać pobrania RAW, a dostępność i utworzenie nie
mogą poprzedzać utworzenia RAW. Daty RAW pozostają deklaracjami capture, nie
niezależnym dowodem czasu zapisu. Nie cofają dostępności nowej obserwacji. Osobno
zweryfikowany dowód wcześniejszego capture może służyć przyszłemu jawnemu eksportowi
rekonstrukcji, ale nie zmienia obecnego kontraktu historii.

RecordedAtUtc to INSERT, nie COMMIT. Późny commit nadal może zmienić ponowiony
wynik dla cutoffu po INSERT. Nie gwarantujemy snapshotu commit-time ani stałego
datasetu między stronami. Ufamy zegarowi bazy i zwykłym uprawnieniom.

## Źródła i adaptery

Kontrakt statusu operacyjnego należy do Application, odczyt do Infrastructure.
Status jest czytany bez cache przed polityką i ponownie przed budżetem/wykonaniem.
Brak źródła lub IsEnabled=false zwraca PermissionDenied z source_missing albo
source_disabled, bez wywołania adaptera i bez zajęcia budżetu. Approved nie omija
tej kontroli. Historyczna ocena praw pozostaje niezależna. Nie zatrzymujemy już
działającego adaptera po zmianie statusu.

Null, niespójność sukces/błąd, niezdefiniowana kategoria, pusty kod i błędny RetryAfter
zwracają InvalidResponse. RetryAfter jest nieujemny, reprezentowalny i dotyczy
RateLimitExceeded. Nieoczekiwany wyjątek wykonania ma stały niesekretny kod
provider_execution_failed. Cancellation wywołującego propaguje się; timeout,
lease concurrency i brak automatycznych retries pozostają zachowane.

## RAW i retencja

Trigger bazy blokuje UPDATE, DELETE i TRUNCATE metadanych RAW, także EF bulk i SQL.
INSERT oraz FK RESTRICT pozostają. Ochrona nie oznacza niezmienności magazynu
obiektowego. Administrator/owner może wyłączyć triggery.

Przyszłe role API/Worker nie mogą być właścicielem, superuserem ani członkiem roli
utrzymaniowej. Otrzymują tylko potrzebne SELECT/INSERT i jawne UPDATE tabel
modyfikowalnych; bez RAW UPDATE/DELETE/TRUNCATE i DDL. Migracje i maintenance
wymagają oddzielnych kont. Migracja nie provisionuje produkcyjnych ról.

Retencja wymaga jawnej autoryzacji, podstawy i zakresu, zewnętrznego chronionego
audytu oraz manifestu koordynującego payloady i zależne metadane. Przed pracą trzeba
zaplanować backup/restore i obowiązki usuwania kopii, wyciszyć zapisy, a po pracy
zweryfikować i przywrócić zabezpieczenia. Uprzywilejowane operacje są oddzielną
kontrolowaną procedurą; nie ma przełącznika aplikacyjnego, bypassu ani purge job.

## Migracja

20261008014627_AuditRemediation jest addytywna. Stare obserwacje otrzymują czas
migracji jako konserwatywną dostępność DB; stare daty, RAW, tożsamości i korekty
pozostają zachowane. Wcześniejsze zapytania wykluczają je. Starych relacji czasowych
RAW nie poprawiamy ani nie odrzucamy wstecznie; nowe INSERT podlegają walidacji.
Volatile default może przepisać tabelę i wymaga silnych blokad: przejrzyj SQL,
wielkość tabeli, okno maintenance i backup. Host nie migruje automatycznie.
Rollback usuwa nowe bariery i wymaga świadomego przeglądu.

Testy używają jednorazowego PostgreSQL 17, bez pomijania przy braku Docker.
BS-005 może rozpocząć ograniczoną dozwoloną integrację po przejściu bramek;
prawa providera, magazyn payloadów, audyt wykonań, realizacja retencji i stały dataset
nadal wymagają osobnych decyzji. Nie dodano providera ani pobierania danych.
