# BetStats 2.0

Wielosportowa platforma danych, predykcji probabilistycznych, symulacji oraz operacji wspomaganych przez AI.

> **Status:** fundament inżynierski BS-001. Dostępne są endpoint liveness API,
> placeholder Web i host Workera. Funkcje sportowe, persystencja, predykcje,
> uwierzytelnianie i lokalizacja nie zostały jeszcze zaimplementowane.

## Granica produktu

BetStats **nie jest bukmacherem**. Nie przyjmuje prawdziwych stawek, depozytów ani wypłat i nie wykonuje zakładów. Prediction Playground korzysta wyłącznie z wirtualnych kuponów do analizy, edukacji i zabawy.

## Planowany pierwszy vertical slice

`Football → jedna rozgrywka → jeden dozwolony darmowy provider → RAW → Observation → Canonical → FeatureSnapshot → PredictionSnapshot → Evaluation`

Przed zmianami przeczytaj `AGENTS.md`. Dane zewnętrzne podlegają własnym licencjom i nie są objęte licencją kodu repozytorium.

## Wymagania i weryfikacja

Stabilny SDK .NET 10, minimum `10.0.100`. `global.json` dopuszcza nowsze pasma
funkcjonalne 10.0 (`latestFeature`), bez wersji prerelease. Restore wymaga dostępu
do NuGet.org. Z katalogu głównego repozytorium uruchom:

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
```

Wersje pakietów są zarządzane centralnie w `Directory.Packages.props`.
`Directory.Build.props` ustawia nullable, implicit usings, deterministyczne
kompilacje, analyzery SDK i traktowanie ostrzeżeń jako błędów.

## Struktura i testy

- `src/BetStats.Domain`: przyszłe reguły domenowe; brak zależności projektowych.
- `src/BetStats.Application`: przyszłe przypadki użycia; zależy od Domain.
- `src/BetStats.Infrastructure`: przyszłe adaptery; zależy od Application i Domain.
- `src/BetStats.Api` i `src/BetStats.Worker`: composition roots, mogą składać Application, Infrastructure i Domain.
- `src/BetStats.Web`: prezentacja; może zależeć od Application i Domain, bez Infrastructure i persystencji.
- `tests/BetStats.UnitTests`: projekt dla Domain i Application; obecnie testuje pierwszeństwo konfiguracji hostów.
- `tests/BetStats.ArchitectureTests`: sprawdza ocenione przez MSBuild zależności w Debug i Release, cykle i obejścia granic.

Obowiązuje modular monolith oraz [ADR 0013](docs/adr/0013-project-dependency-direction.md).
Nie dodano sztucznej logiki domenowej. Testy architektury wymagają checkoutu
źródeł i SDK. CI wykonuje restore, build Release i testy; nieudany test zatrzymuje
CI. CodeQL zachowuje ręczną kompilację z BS-000. Docker nie jest wymagany do testów.

## Praca lokalna i konfiguracja

Pracuj na gałęzi od `main`, w zakresie Issue. Dodaj testy i dokumentację,
uruchom powyższe polecenia i otwórz PR do `main`. Scalanie wymaga przeglądu.
Host uruchom np. przez `dotnet run --project src/BetStats.Api`; analogicznie
działają Worker i Web.

Domyślne hosty wczytują opcjonalne `appsettings.json`, JSON dla środowiska,
zmienne środowiskowe, a następnie argumenty polecenia. Zmienne nadpisują JSON,
a argumenty nadpisują zmienne. Zagnieżdżone klucze zapisuj z `__`, np.
`Logging__LogLevel__Default=Information`. Do pracy lokalnej ustaw
`DOTNET_ENVIRONMENT=Development` dla Workera lub
`ASPNETCORE_ENVIRONMENT=Development` dla API/Web.

Sekrety przekazuj przez środowisko; nie zapisuj ich w repozytorium. `.env`,
lokalne pliki ustawień, certyfikaty, wyniki kompilacji i lokalne dane są ignorowane.
`appsettings.*.local.json` nie są automatycznie wczytywane. .NET nie wczytuje
automatycznie `.env`; ten plik służy Docker Compose. `.env.example` zawiera
wyłącznie publiczne placeholdery deweloperskie.

Opcjonalny PostgreSQL: skopiuj `.env.example` do ignorowanego `.env`, zmień hasło
i wykonaj `docker compose up -d`. Port jest dostępny wyłącznie przez `127.0.0.1`.
Aplikacja nie łączy się jeszcze z bazą; przykładowa zmienna connection string
jest przeznaczona dla przyszłej integracji. EF Core, Testcontainers,
OpenTelemetry i podsystem ML są planowane, ale jeszcze niezintegrowane.

## Dokumentacja

- 🇬🇧 [Software & Product Engineering Specification v1.0 — English](docs/specifications/BetStats_2_0_Software_Product_Engineering_Specification_v1_0_EN.docx)
- 🇵🇱 [Specyfikacja Produktu i Inżynierii v1.0 — Polski](docs/specifications/BetStats_2_0_Specyfikacja_Produktu_i_Inzynierii_v1_0_PL.docx)
- ADR: `docs/adr/`
- Dokumentacja angielska: `docs/en/`
- Dokumentacja polska: `docs/pl/`

Markdown jest dokumentacją żywą; pliki DOCX są wersjonowanym baseline specyfikacji.
