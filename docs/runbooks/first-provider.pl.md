# BS-018 — dostawca, canary i odzyskiwanie

**Aktywacja live jest zablokowana.** Dostarczono granicę API-Football do testów na
atrapach HTTP. Brakuje zweryfikowanej zgody, klucza operatora i zatwierdzonej
integracji trwałego JSON RAW z danymi kanonicznymi. Nie ma polecenia włączenia ani
pobrania live. Sam klucz lub zmienna środowiskowa niczego nie aktywuje.

## Polecenia dostępne teraz

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx -c Release --no-restore
dotnet test tests/BetStats.UnitTests -c Release --no-build --filter FullyQualifiedName~ApiFootballCanaryTests
dotnet test tests/BetStats.IntegrationTests -c Release --no-build --filter FullyQualifiedName~ApiFootballBoundaryTests
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Web --no-launch-profile -- --urls http://127.0.0.1:5081
```

Pod lokalnym adresem wypisanym przez aplikację otwórz
`/provider-readiness?lang=pl-PL`. Strona opisuje blokady wydania, nie odczytuje
sekretów ani stanu konta. Brak rzeczywistych zegarów pobierania, limitu dostawcy,
nadchodzących spotkań i ocen pozostaje jawny.

Istniejące polecenia w instrukcjach [BS-017](authorized-acquisition.pl.md),
[harmonogram/predykcje/oceny](automated-pipeline.pl.md) i
[walidacja](real-data-validation.pl.md) nadal obsługują uprawnione lokalne dane.
Nie uruchamiają nowego adaptera i nie interpretują jego JSON jako historycznego CSV.

## Warunki przyszłej aktywacji

1. Operator musi dostarczyć dowód praw do pobierania, RAW, przechowywania, analizy
   i potrzebnych wyników/modelowania/wyświetlania. Należy osobno ustalić retencję,
   atrybucję i prawa stron trzecich; darmowe konto nie zastępuje zgody.
2. Zapisać wersjonowaną politykę jako draft, a następnie zatwierdzić ją zgodnie
   z istniejącą kontrolą źródeł. Nie tworzyć fikcyjnych grantów ani decyzji operatora.
3. Potwierdzić uprawnienia konta do jednych rozgrywek/sezonu, okna <=7 dni oraz
   limitów żądań. Klucz wyłącznie przez środowisko/magazyn sekretów; proponowana
   nazwa `BETSTATS_API_FOOTBALL_KEY` **nie jest obecnie odczytywana** przez aplikację.
4. Zaimplementować zatwierdzone połączenie adaptera z istniejącą operacją BS-015:
   trwały oryginalny JSON RAW przed parsowaniem, hash, zakres, źródło/polityka,
   zegar bazy, audyt, przegląd tożsamości, dowody kickoff i wyników. Nie utożsamiać
   odpowiedzi w pamięci z trwałą obserwacją. Sprawdzić przerwanie, konkurencję,
   idempotencję, cofnięcie praw i wyłączenie na izolowanym PostgreSQL.
5. Dopiero po zatwierdzeniu jawnie zaplanować/włączyć mały canary przez istniejący
   harmonogram. Bez pobierania podczas startu, bez drugiego harmonogramu. Limit
   żądań jest procesowy i resetuje się przy restarcie; wymaga rozwiązania przed
   autonomicznym pobieraniem. Nie zastępować chronionej operacji ręcznym curl.
6. Sprawdzić rzeczywisty RAW i monitor BS-017. Istniejącą predykcję około 12 godzin
   przed kickoff uruchamiać wyłącznie po przejściu bramek historii, czasu, pokrycia,
   jakości i rozgrzewki. Cutoff to faktyczny czas bazy; bez cofania zegarów.
7. Po meczu zebrać niezależnie zaobserwowany wynik FT i uruchomić istniejącą ocenę
   wskazując oryginalną zamrożoną predykcję. Brak wyników lub kwalifikacji pozostaje
   wykluczeniem; bez fikcyjnych metryk skuteczności.
8. Wyłączenie przyszłego pobierania: istniejące `Pipeline:Action=disable`, JobId
   i jawna decyzja operatora według instrukcji BS-015, przełącznik dostawcy=false,
   a w razie potrzeby cofnięcie polityki. W tym wydaniu nie istnieje harmonogram live.

## Błędy i odzyskiwanie

401/403: zatrzymać i sprawdzić konto bez wypisywania klucza/treści. 429: istniejący
cooldown Retry-After, bez automatycznych ponowień. Timeout/499/5xx: bezpieczna
klasyfikacja; ewentualne ponowienie tylko w limicie prób istniejącej operacji.
Błędny JSON lub konflikt ID: zachować RAW, odrzucić interpretację, przeprowadzić
jawny przegląd. Nieznany status: odrzucić. PST/CANC/TBD/AET/PEN nie są zwykłym
FT ani dowodem kwalifikacji. Brak jednoznacznych sekund UTC lub zgodnego timestamp:
kickoff niedostępny. Wielostronicowa odpowiedź fixtures: kontrakt nieobsługiwany,
nie dodawać nieudokumentowanego parametru page. Nieznane prawa, atrybucja lub
nieobsługiwana retencja blokują operację.

Odzyskiwanie kolekcji live wymaga przyszłej zatwierdzonej integracji. Obecne lokalne
operacje korzystają z istniejących trwałych identyfikatorów dzieci i blokad właściciela.
Nie przepisywać RAW, nie usuwać zamrożonych predykcji, nie zmieniać zegarów i nie
oznaczać odtworzonego canary jako rzeczywistej predykcji sprzed meczu.

Szczegóły kontraktu i konfiguracji: [instrukcja EN](first-provider.en.md),
[macierz dostawców](../en/data/provider-decision-matrix.md).
