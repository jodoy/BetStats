# BS-009 verification

Local verification used Windows, .NET SDK 10.0.401 and Docker with real
PostgreSQL 17 containers. No existing development database was migrated or seeded.

## Commands and results

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger "trx;LogFileName=bs009-final.trx"
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

Restore succeeded. Release build succeeded with zero warnings and errors.
The test run passed 491 cases: 211 unit, 14 architecture and 266 integration;
zero failed and zero skipped. The original 439 cases remain in the suite.
EF reported no model changes since the last migration.

Ignored local logs are under `artifacts/logic-audit/bs009-final-{restore,build,test,model}.log`;
TRX files are under each test project's `TestResults/bs009-final.trx`.
GitHub CI and CodeQL run links and their final PR-head status are recorded in the PR.

## Evidence

- PostgreSQL fixtures apply the complete migration chain to fresh databases.
  A separate upgrade test starts at BS-008.1, preserves finalized v1/v2 artifact
  bytes, hashes and recording clocks, and applies the additive tenth migration.
  Existing dataset workflow tests exercise v1/v2 build and verification semantics.
- Result tests cover 2:1, 0:0, half-time/full-time, unknown half-time, half-time-only
  labels, postponed/cancelled events, transitions, conflicts across sources and
  aliases, simultaneous conflicting reports, explicit later corrections, historical
  reads before corrections, replay, original season rejection and late arrival.
- Historical reads enforce both clocks, reviewed identity, source rights,
  retention and verified RAW. Tests revoke source permission and remove/corrupt RAW.
  Private reports and unregistered fixture hashes are excluded from public reads.
- Dataset tests establish reproducible v3 hashes, partial observed coverage and
  target-label separation from feature evidence and hashes. SQL guards reject
  UPDATE, DELETE and TRUNCATE on both new tables.
- A real Worker child process runs the explicit Development demo against disposable
  PostgreSQL and temporary RAW storage: successful/reused/successful/reused imports,
  seven result observations, four RAW envelopes and eight ingestion audits.
- WebApplicationFactory tests exercise the four fictional GET routes, pagination,
  historical cutoffs, sanitized schemas, error responses and Production exclusion.
  The carried local Swagger fix opens the HTTP profile at `swagger` in Visual Studio.

## Operational limits

Apply migrations and approve the fictional demo explicitly; startup does neither.
Real provider transport, production UI, authentication, model training and scoring
are outside this task. V3 dataset assembly is an application port without an
operator CLI or separate recovery ledger. Observed statistics remain partial;
metadata coverage never certifies result completeness. Fixtures use a fictional
2026 timeline. See [the result runbook](../data/football-results-outcomes.md) and
[ADR 0023](../../adr/0023-football-results-and-outcome-provenance.md) for semantics
and BS-010 recommendations.
