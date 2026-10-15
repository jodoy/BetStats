# Autoryzowany canary dostawcy w Development

Status wdrożenia: **LIVE_CANARY_BLOCKED**. Nie dostarczono rzeczywistej zgody,
uprawnień konta, polityki źródła i zakresu/limitu, klucza ani zgody na wykonanie
rzeczywistego canary. Testy HTTP są syntetyczne.

Najpierw wykonaj [listę kontroli](../en/data/provider-activation-checklist.md) oraz
[konfigurację](../en/data/api-football-configuration.md). Polecenia PowerShell w
[instrukcji EN](provider-canary.en.md) są dokładnymi poleceniami wspólnymi dla obu
języków. Nie umieszczaj klucza w czacie, plikach repozytorium ani argumentach CLI.
Ustaw `DOTNET_ENVIRONMENT=Development`; klucz pochodzi wyłącznie z
`BETSTATS_API_FOOTBALL_KEY`, a flaga z `BETSTATS_API_FOOTBALL_ENABLED=true`.

1. Przygotuj poza Git rzeczywistą, zatwierdzoną zgodę w `provider-grant.json`.
   Zgoda musi obejmować cały oryginalny JSON fixtures, RAW, trwałe przechowywanie,
   analitykę oraz konkretną ligę/sezon/okno i kategorię. Ograniczona retencja i
   niespełniona atrybucja blokują aktywację. Deklaracja operatora nie jest
   niezależną certyfikacją praw.
2. `provider-register --Pipeline:GrantPath=...` zapisuje zgodę w stanie wyłączonym.
   `provider-status --Pipeline:SourceId=...` sprawdza stan i budżet;
   `provider-plan --Pipeline:GrantId=...` zwraca przypięty hash planu.
3. Dopiero po jawnej zgodzie na rzeczywisty canary wykonaj
   `provider-activate --Pipeline:GrantId=...`, następnie `plan`, `enable` i
   `run-once` według EN. Wszystkie mutacje wymagają `--Pipeline:Actor`,
   `--Pipeline:Reason`, `--Pipeline:Approve=true`.
4. `run-once` wybiera globalnie jedno oczekujące zadanie. Użyj wydzielonej bazy
   Development bez innych włączonych zadań. Nie ustawiaj cyklicznego harmonogramu.
5. Pobranie zapisuje RAW przed parsowaniem i przygotowuje wyłączone zadanie
   publikacji. Sprawdź hash, rzeczywisty zegar bazy i zakres. Stan Completed
   pobrania nie oznacza gotowości publikacji ani predykcji.
6. Włącz rzeczywisty `PublicationJobId` i wykonaj `run-once`.
   `provider-publication-inspect --Pipeline:JobId=...` pokazuje blokady;
   `provider-publication-verify --Pipeline:ReceiptId=...` weryfikuje potwierdzenie.
   Przy ReviewRequired użyj [istniejącego przeglądu tożsamości](provider-publication.pl.md)
   i ponów publikację z tego samego RAW. Nie pobieraj ponownie z powodu przeglądu.
7. Nowe pobranie wyników wymaga zatwierdzonej kategorii PostmatchResults i nowego
   jawnego zadania. Korekty są dopisywane, predykcje i kohorty pozostają niezmienne.
   Używaj istniejących bramek pokrycia, historii, rozgrzewki i czasu odcięcia.
   Niepełne dowody oznaczają Blocked/Ineligible; nie twórz zastępczych metryk.
8. Wyłącz dostawcę poleceniem `provider-disable --Pipeline:GrantId=...` oraz usuń
   lokalny klucz/flagi zgodnie z [odzyskiwaniem i bezpieczeństwem](provider-canary-recovery-security.md).
   Zachowaj lokalny raport rzeczywistych dowodów i zużytego budżetu poza Git.

Monitor tylko do odczytu:
`/monitor/provider-activation/<UUID źródła>?lang=pl-PL` lub `?lang=en-GB`.
GET nie kontaktuje się z dostawcą. Pozostały limit dostawcy jest nieznany.
Budżet lokalny jest trwałym limitem całej próby źródła, bez resetu przy restarcie,
zmianie zgody lub północy. Zgoda nie może udawać nieistniejących praw.
