# BS-019A: publikacja z oryginalnego RAW

Używaj wyłącznie Development, zatwierdzonego połączenia PostgreSQL i katalogu RAW poza
repozytorium. Migrację `20261014115707_DurableProviderPublication` zastosuj jawnie istniejącą
procedurą. Startup nie wykonuje migracji ani zadań. Workflow odczytuje istniejący RAW profilu
`api-football-fixtures-v1`; nie pobiera nowych danych, nie używa kluczy ani transportu HTTP.

## Plan i wykonanie

Podaj prawdziwy identyfikator wcześniej zatwierdzonego RAW. Poniższy zakres jest fikcyjny,
nie oznacza przyznanych uprawnień. Operator i powód są deklaracjami audytowymi.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$raw = '<UUID istniejącego zatwierdzonego RAW>'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved original RAW publication', '--Pipeline:Approve=true')
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-plan --Pipeline:RawId=$raw --Pipeline:League=999999 --Pipeline:Season=2026 --Pipeline:From=2026-10-13 --Pipeline:To=2026-10-19 --Pipeline:EvidenceKind=Synthetic @audit
```

Zapisz rzeczywistą odpowiedź JSON w `C:\approved-inputs\publication-plan.json`.
Nie wymyślaj hashy, wersji polityk, zegarów ani pochodzenia. Utwórz definicję BS-015:

```powershell
$input = (Get-Content C:\approved-inputs\publication-plan.json -Raw | ConvertFrom-Json).Result
$due = [DateTime]::UtcNow
$due = $due.AddTicks(-($due.Ticks % 10))
@{
  Version = 1; Kind = 'ProviderPublication'
  PayloadJson = ($input | ConvertTo-Json -Depth 30 -Compress)
  Schedule = @{ FirstDueUtc = $due.ToString('o'); TimeZone = 'UTC'; DaylightSavingPolicy = 'NotApplicable' }
  MaximumAttempts = 3; LeaseSeconds = 300; PredictionHorizonSeconds = 43200
} | ConvertTo-Json -Depth 30 | Set-Content C:\approved-inputs\publication-job.json -Encoding utf8
$job = [Guid]::NewGuid().ToString()
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=C:\approved-inputs\publication-job.json @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-inspect --Pipeline:JobId=$job @audit
```

`run-once` pobiera jedno najbliższe włączone zadanie z globalnej kolejki; nie wybiera zadania
według lokalnej zmiennej. Samo enable niczego nie uruchamia. Dostępny jest też ograniczony
czasowo i liczbowo worker BS-015 `work`. Mutacje wymagają Approve=true. Plan, inspekcja i
weryfikacja są odczytami. Limity: RAW 1 MiB, jedna strona, 500 meczów, 1-5 prób, lease
1-1800 sekund. Wybierz lease odpowiedni do wielkości partii; wygaśnięcie wycofuje zapis.
Status pokazuje maksymalnie 100 receiptów, wykonań i zdarzeń audytu.

## Review i recovery

Wymagane są zatwierdzone powiązania rozgrywek, sezonu, gospodarzy, gości i meczu. Publisher
nie dopasowuje nazw i nie tworzy domyślnych encji kanonicznych. Nieznane identyfikatory
otrzymują decyzję nierozstrzygniętą z powiązaniem do oryginalnego RAW i receipt ReviewRequired.
Użyj istniejących poleceń review, z rzeczywistym rodzajem i UUID zatwierdzonego celu:

```powershell
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Quality:Action=list --Quality:SourceId='<UUID źródła>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Quality:Action=review --Quality:IdentityId='<UUID tożsamości>' --Quality:SourceId='<UUID źródła>' --Quality:Decision=Approve --Quality:TargetKind=Participant --Quality:TargetId='<UUID zatwierdzonego celu>' --Quality:ExpectedVersion=1 --Quality:OperatorId=operator:local --Quality:Reason='Explicit exact reviewed binding'
$execution = '<UUID wykonania z inspect>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=retry --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=recover --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-verify --Pipeline:ReceiptId='<UUID receiptu>' @audit
```

Retry dotyczy wykonań zablokowanych, nieudanych lub anulowanych po usunięciu przyczyny.
Recover dotyczy wygasłego Running bez aktywnej sesji właściciela. Oba zużywają pierwotny
budżet prób. Wyczerpanie prób zapisuje Blocked bez ponownego wykonania. Recovery odczytuje
ten sam UUID, hash i zegary RAW; nie pobiera danych ani nie otwiera ponownie pliku dostawcy.
Zmiana zatwierdzonych decyzji daje nowy receipt. Korekta celu opublikowanego meczu wymaga
nowszego RAW i tworzy nowe obserwacje, zachowując historyczne powiązania.

Obserwacje i autorytatywny receipt są zatwierdzane atomowo. Potwierdzenie nadrzędnego pipeline
stanowi oddzielny etap. Awaria pomiędzy etapami nie usuwa zatwierdzonego receiptu: recovery
wykorzystuje go ponownie. Weryfikacja używa niezależnego połączenia z bazą. Brak uprawnień lub
integralności oznacza odmowę. Publikacja i odwołanie praw są porządkowane blokadą rekordu
źródła; wcześniej zatwierdzone odwołanie praw blokuje publikację.

## Monitoring i dowody

Odczyty Development-loopback, z aktualnym prawem wyświetlania:
`/api/v1/dashboard/publication/<UUID receiptu>` oraz
`/api/v1/dashboard/publication-status/<UUID zadania>`. Widoki:
`/monitor/publication/<UUID receiptu>?lang=pl-PL` i
`/monitor/publication-status/<UUID zadania>?lang=en-GB`; oba obsługują oba języki.
GET nie publikuje, nie importuje, nie naprawia, nie uruchamia recovery, modeli ani ewaluacji.
POST jest niedostępny. Raporty nie ujawniają ścieżek magazynu ani treści JSON dostawcy.

BS-016 przyjmuje profil `api-football-fixtures-v1` z oryginalnym RAW, hashem, zakresem i
klasyfikacją pochodzenia. Raporty BS-017 mogą wskazywać UUID zakończonej publikacji. Nadal
obowiązują pokrycie, warm-up, as-of, cutoff i zamrożenie predykcji przed kickoffem. Parsowanie
interpretuje JSON; RAW zachowuje bajty; publikacja zapisuje zatwierdzone obserwacje;
kwalifikacja prospektywna wymaga niezależnych dowodów; predykcje i ewaluacja są oddzielnymi
workflow. Testy syntetyczne nie potwierdzają zgodności z rzeczywistym dostawcą ani skuteczności.

Szczegóły: [ADR](../adr/0033-durable-provider-publication.md),
[macierz awarii](provider-publication-recovery.md), [weryfikacja](../en/quality/bs-019a-verification.md).
