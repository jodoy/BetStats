# Publikacja dostawcy: stan przygotowania

BS-019A nie jest ukończone operacyjnie. Dostępne są audyt i kontrakt mapowania;
publikacja JSON do domeny kanonicznej, trwałe receipty i recovery nie są zaimplementowane.
Nie włączaj profilu JSON w publisherze CSV ani transportu HTTP dostawcy.

Dotychczasowe polecenia BS-015/017 obsługują wyłącznie opisane wcześniej operacje.
To przygotowanie nie dodaje poleceń `provider-publication-plan`, `dispatch`, `verify`
ani `recover`. Żądania GET nie publikują i nie uruchamiają recovery.

Przed uruchomieniem publikacji wymagane są kontrole bieżących uprawnień, możliwości
dostawcy i zakresu; wersje zatwierdzonych decyzji tożsamości; atomowy zapis obserwacji,
wyników i receiptów; fencing właściciela pipeline; recovery z oryginalnego RAW oraz
monitoring PL/EN. Mutacje wymagają jawnej intencji operatora. Sam klucz API ani przyszły
kickoff nie dowodzą autoryzacji lub kwalifikowalności prospektywnej.

Projekt recovery: po zapisie RAW odczytaj i zweryfikuj ten sam manifest; po parsowaniu
ponownie odczytaj te bajty; nieudana transakcja musi wycofać wszystkie zapisy kanoniczne;
po commit bez potwierdzenia wykorzystaj oryginalny receipt. Nieaktualny, anulowany lub
wygasły właściciel nie może publikować; wyczerpanie prób musi zatrzymać wykonanie.
Te scenariusze nadal wymagają testów na jednorazowym PostgreSQL.

Parsowanie interpretuje JSON. Zapis RAW zachowuje bajty i oryginalne zegary. Publikacja
kanoniczna zapisuje obserwacje po weryfikacji tożsamości. Kwalifikowalność prospektywna
wymaga ponadto kontroli as-of, uprawnień, pokrycia, warm-up i cutoff. Publikacja zamrożonych
predykcji i ewaluacja pomeczowa pozostają oddzielnymi istniejącymi operacjami.
