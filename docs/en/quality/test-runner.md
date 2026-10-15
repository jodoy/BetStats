# Windows test runner

PowerShell 7 + .NET 10; Docker required for PostgreSQL sets.

```powershell
./eng/test.ps1                     # Fast
./eng/test.ps1 -Suite Fast
./eng/test.ps1 -Suite Integration
./eng/test.ps1 -Suite Migration
./eng/test.ps1 -Suite Security
./eng/test.ps1 -Suite Full
```

Default performs locked restore + Release build first. `-NoBuild` is only for an
already built exact revision. Fast selects unit and architecture projects (no
Docker); Integration selects integration cases excluding Category=Migration;
Migration selects that category; Security selects Category=Security across all
three projects. Categories may overlap. Full executes each project once without
filters, so overlapping categories never repeat tests in Full.

All existing tests remain. Category traits are on relevant classes plus upgrade
cases in mixed test classes. Security contains governance/provider/ownership,
revocation, concurrency, temporal and recovery boundaries. Migration includes
fresh/upgrade schema checks and DB+RAW/role restore drills. Fast is not a substitute
for Full before changes to schemas, provider execution, permissions or recovery.

Results live under ignored artifacts/tests/SUITE-UUID: per-project TRX plus a
summary.json with counts and elapsed seconds. Missing TRX or zero selected cases
throws; failed native commands preserve their exit code; failed or skipped tests
return failure. Invalid Suite values are rejected by parameter validation.
Isolated script contract tests exercise zero selection, nonzero exit propagation
and exactly three distinct project selections for Full with a mock dotnet process.
These contract tests do not replace actual suite execution evidence.

PostgreSQL fixtures remain isolated per class, with separate disposable upgrade
containers as before. No shared mutable DB or runtime security bypass was added.
See [BS-019C verification](bs-019c-verification.md) for measured suite times.
