# Backup i odtwarzanie PostgreSQL + RAW — Development

Wymagania: PostgreSQL 17, lokalny Docker, PowerShell 7 i kompilacja Release.
Pełna specyfikacja: [procedura EN](backup-restore.en.md).

1. Zatrzymaj API, Web, Worker, migracje oraz wszystkie procesy utrzymania RAW.
   Sprawdź, że inni operatorzy nie zapisują danych ani nie zmieniają schematu.
2. W prywatnym PowerShell ustaw administracyjne `ConnectionStrings__BetStats`
   i `Ingestion__RawStoragePath`. Nie wpisuj haseł w argumentach polecenia.
3. Wykonaj backup do NOWEGO katalogu poza Git:

```powershell
./eng/backup.ps1 -Container NAZWA_KONTENERA -User betstats_admin `
  -Destination "$env:LOCALAPPDATA/BetStats/backups/NOWA_KOPIA" -OfflineConfirmed
```

Flaga potwierdza zatrzymanie procesów; skrypt sam ich nie wykrywa. Blokady SHARE
blokują DML istniejących tabel podczas wspólnego snapshotu i kopiowania plików.
Nie chronią przed administratorem zmieniającym schemat ani zewnętrznym zapisem
plików. Nie deklarujemy spójności przy naruszeniu warunku offline.

Backup zawiera dump bazy, manifest identyfikatorów i SHA-256 oraz oryginalne RAW.
Powiązane pliki `.pending` są zachowane w kopii jako `.raw`; pliki źródłowe nie
są usuwane. Niepowiązane staged pozostają w źródle i nie są certyfikowane przez
snapshot. Brakujące długości starszych manifestów blokują procedurę.

Tylko kod sukcesu, znacznik COMPLETE i poprawne hashe pozwalają uznać kopię
za kompletną. Kopie są prywatne, poza repozytorium; eksport wymaga ochrony
i szyfrowania. Hash nie chroni przed właścicielem celowo przepisującym manifest.

Odtwarzanie do NOWEGO izolowanego środowiska:

```powershell
./eng/restore.ps1 -Backup "$env:LOCALAPPDATA/BetStats/backups/POPRAWNA_KOPIA" `
  -Destination "$env:LOCALAPPDATA/BetStats/restore/NOWA_PROBA"
```

Powstaje nowy kontener PostgreSQL 17, losowe hasło i port na loopback, bez wolumenu.
Skrypt sprawdza kopię przed restore, potem zgodność DB/RAW, identyfikatory, długości,
hashe i model EF. Nie nadpisuje istniejącej bazy ani katalogu. W tym PowerShell
zmienia zmienne połączenia na izolowaną bazę i wyłącza flagę API-Football.
Kontener pozostaje do inspekcji; usuń wyłącznie wypisaną instancję testową.

Przed runtime skonfiguruj ograniczone role. Sprawdź aktualne zgody, lease, jobs
i receipts. Recovery ma użyć oryginalnego RAW, bez nowego HTTP. Niepewnych prób
nie wolno usuwać ani refundować w celu ponownego pobrania.

RPO zależy od wieku ostatniego poprawnego snapshotu i częstotliwości kopii.
RTO wymaga pomiaru na reprezentatywnej kopii; początkowa mała próba syntetyczna
trwała około 21 s i nie stanowi gwarancji dla rzeczywistych danych. SnapshotUtc
jest czasem obserwacji, a nie gwarantowaną granicą wszystkich commitów.

Awaria: zatrzymaj zapis, zachowaj źródła i wadliwą kopię, zapisz bezpieczne ID
i kategorię problemu. Odtwórz ostatnią zweryfikowaną kopię w izolacji. Nie poprawiaj
hashy, nie usuwaj pending i nie pobieraj danych ponownie, aby ukryć brak RAW.
Procedura nie wykonuje automatycznego przełączenia aplikacji na odtworzoną bazę.
