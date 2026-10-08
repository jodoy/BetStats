# BS-011 verification

Base: main `0efd98f154fd05928d488cdc3fe6860d8bcb6306`, after GitHub confirmed
PR #14 merged. BS-010 head `a45c458517f44c09fb18ccb54e1508139b988454` has successful
CI #37 and CodeQL #35, including SARIF processing. No predecessor dependency remains.
Branch: `feat/bs-011-historical-backtesting`. No pull request is merged by this task.

Environment: Windows, .NET SDK **10.0.401**, Docker, disposable **PostgreSQL 17**.
No user's existing development database was migrated or seeded. No real provider,
credential, odds or restricted dataset was introduced.

## Exact commands and final local results

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger "trx;LogFileName=bs011-verified.trx"
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

| Check | Result |
|---|---|
| Restore | Succeeded, all projects up to date; exit 0 |
| Release build | Succeeded, 0 warnings, 0 errors; exit 0 |
| Unit tests | 257 passed, 0 failed, 0 skipped |
| Architecture tests | 17 passed, 0 failed, 0 skipped |
| PostgreSQL/integration tests | 311 passed, 0 failed, 0 skipped |
| Total | **585 passed**, all 539 BS-010 cases retained and 46 added; exit 0 |
| EF model | No changes since last migration; exit 0 |
| Fresh PostgreSQL 17 | All 12 migrations applied; no pending model/migrations |
| Upgrade from BS-010 | Preserved existing v1/v2/v3 bytes, hashes and trusted recording clocks |

Ignored local logs: `artifacts/logic-audit/bs011-final-{restore,build,test,model}.log`.
TRX files: each test project's `TestResults/bs011-verified.trx`.
The final commit SHA and actual-head CI/CodeQL run URLs/outcomes are recorded in
the PR description and delivery summary after remote runs finish. Remote success
must not be inferred from local tests. Existing workflow files are unchanged.

## What is verified

- All six supported football targets, deterministic synthetic baselines and immutable
  historical simulation records with independent feature/input hashes and dataset refs.
- Prediction-time leakage fails hard: future availability/recording, later identity
  reviews, retrospective feature interpretation, target outcomes and corrupted hashes.
  Later labels/corrections do not change frozen predictions. Higher evaluation versions
  explicitly admit supported correction versions without rewriting old artifacts.
- Date-only and source-justified precise kickoff paths, minimum horizons, precise end,
  historical cutoffs, late labels, missing/partial/conflicting evidence, first-half
  justification and rescheduled dates. Unverified precise-time assertions cannot become
  source-bound kickoff. Strict evaluation coverage is recalculated for the exact
  observation types, source, participants and window, independently of partial observed
  feature gates. Later coverage-review clocks cannot enter prediction interpretation.
- Accuracy ties, binary/multiclass Brier, natural Log Loss, explicit infinity without
  epsilon, MAE, calibration endpoints/empty bins, empty denominators, sample order and
  the separately reported 100-sample threshold. No arbitrary normalization is applied.
- Real PostgreSQL concurrent requests/recovery, fingerprint mismatch, live-owner
  protection, expired-owner fencing before/after recovery, absent/unbounded leases,
  cancellation after durable claim, RAW loss/corruption and explicit repair/recovery.
- Current permission revocation denies plan, new run, inspection, finalized replay/
  recovery and RAW access. A controlled PostgreSQL lock test revokes rights **between
  assembly and publication** and proves no artifact commits.
- Trusted DB clocks, canonical SHA-256 content/request bindings, atomic artifact and
  Succeeded publication, content deduplication, SQL UPDATE/DELETE/TRUNCATE rejection,
  current authorization reference preservation and standard/deep verification.
- A real Development Worker child process routes plan/run/inspect/verify-deep/recover;
  operator command tests enforce Development, actor/reason and mutation approval.
- Fresh migrations plus an explicit BS-010 upgrade preserve opaque historical artifact
  bytes/hashes/clocks. Existing workflow tests verify executable v1/v2, legacy v3 and
  extended v3 compatibility. Published migrations, old serializers/calculators, project/
  package references, production routes and startup behavior are unchanged.

## What is not claimed

Positive eligible historical evaluation scenarios are **pure clock-controlled unit
fixtures**. PostgreSQL operational scenarios use actual recording clocks and fresh
future targets, honestly publishing explained zero-eligible reports. No positive
end-to-end evaluation over authentic pre-event PostgreSQL provider history is verified;
that requires permitted history, independent complete inventories and source-bound
end evidence recorded over time. No database clock or RAW capture is backdated.
No real provider completeness or trained-model performance is asserted.

Runtime must use non-owner roles; owners can bypass triggers. Operator identifiers
are claims, not authentication. Leases have no heartbeat and last ten minutes by
default; DB loss may leave Running and slow operations require explicit recovery.
RAW can disappear after assembly; deep verification reports that separately from
immutable integrity. Historical feature reconstruction and changed result dates are
conservatively denied/excluded. No automatic migrations, imports, evaluation, retries,
recovery or merges occur. See [runbook](../data/historical-backtesting.md) and
[ADR 0025](../../adr/0025-historical-backtesting.md).
