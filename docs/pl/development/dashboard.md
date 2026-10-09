# Uruchomienie dashboardu BS-014

Wymagany .NET 10. Z głównego katalogu repozytorium:

```powershell
dotnet restore BetStats.slnx
dotnet build BetStats.slnx -c Release --no-restore
dotnet run --project src/BetStats.Api -c Release --no-build --no-launch-profile -- --environment Development --urls http://localhost:5080 --Dashboard:DemoEnabled true
```

W drugim terminalu:

```powershell
dotnet run --project src/BetStats.Web -c Release --no-build --no-launch-profile -- --environment Development --urls http://localhost:5081 --Dashboard:ApiBaseUrl http://localhost:5080/
```

Otwórz `http://localhost:5081/`. Zatrzymanie: Ctrl+C w obu terminalach. DEMO jest jawnym, fikcyjnym zbiorem w pamięci; nie wymaga PostgreSQL, nie inicjalizuje bazy i nie deklaruje zaobserwowanej skuteczności. Modele i backtesty pozostają puste, dopóki nie istnieją rzeczywiste zweryfikowane artefakty.

Dla istniejących danych skonfiguruj w terminalu API `ConnectionStrings__BetStats` oraz `Ingestion__RawStoragePath`, uruchom PostgreSQL według istniejącej instrukcji i usuń argument `--Dashboard:DemoEnabled true`. Migracje, import, przegląd tożsamości i publikacja artefaktów wymagają osobnego jawnego workflow operatora. Dashboard nie wykonuje ich podczas startu ani odczytu.

API i Web dopuszczają wyłącznie Development i połączenia loopback z lokalnym Host/Origin. Nie wystawiaj ich przez proxy do sieci; wdrożenie zdalne wymaga osobnego uwierzytelniania. Web zna tylko adres API, bez dostępu do PostgreSQL. Poświadczenia pozostają poza kodem i historią poleceń.

Odczyt wymaga aktualnych praw PublicDisplay, InternalAnalytics, HistoricalRetention i RawPayloadStorage oraz zachowanego RAW ze zgodnym hashem. Cofnięte prawa, przekroczona retencja, brakujące dowody i niespełnione wymagania atrybucji blokują wyświetlanie. Zgoda na analizę nie zastępuje zgody na prezentację.

Zasoby GET pod `/api/v1/dashboard/`: competitions, seasons, teams, fixtures, predictions, models, backtests, quality, provenance. Filtry: Competition/Season/Team (UUID), From/To (yyyy-MM-dd), Status, Offset 0–10000, Limit 1–100 (domyślnie 20). Kolejność jest stabilna, a SQL wybiera klucze strony przed odczytem bajtów artefaktów. Status filtrowany jest według zamrożonego snapshotu; późniejsze dowody wynikowe mogą ujawnić zakończenie lub konflikt. Metryki zawsze dotyczą oryginalnego raportu, bez ponownej ewaluacji wyfiltrowanych próbek.

Jakość podsumowuje pierwsze 100 pasujących meczów, bez certyfikowania kompletności całego źródła. Weryfikacja zapisu nie jest ponownym odtworzeniem modelu. Pełny opis kontraktów znajduje się w `docs/en/development/dashboard.md`, ADR 0028 i raporcie weryfikacji BS-014.
