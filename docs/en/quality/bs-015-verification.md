# BS-015 verification

## Baseline and delivery

PR #18 (BS-014) was merged on 2026-10-09 at 13:19:35 UTC. Its actual final head, `e3bf123d9c8f156dad6a8e07d9e00a62494a91a0`, passed [CI #47](https://github.com/jodoy/BetStats/actions/runs/37933180778) and [CodeQL #45](https://github.com/jodoy/BetStats/actions/runs/37933180776). Branch `feat/bs-015-automated-pipeline` starts from current main, merge commit `8f78cc018e1535e3cc4f7a6544a6bd5ab07e28ae`; there is no unmerged BS-014 dependency.

Production implementation head `ce232ee9eb5f09eb5c4afa46d86b192051ef8ff9` passed the full local suite and [CI #51](https://github.com/jodoy/BetStats/actions/runs/37946243575), plus [CodeQL #49](https://github.com/jodoy/BetStats/actions/runs/37946243580). This report and an additional read-only HTTP canary are the final verification-only changes. The [PR #19 description](https://github.com/jodoy/BetStats/pull/19) records the exact final head and its own CI/CodeQL results; earlier-head checks do not substitute for final-head checks. No merge is performed.

Environment: Windows, .NET SDK 10.0.401, Docker Desktop and disposable PostgreSQL 17 test containers. Tests use fictional, explicitly authorized local inputs and real PostgreSQL clocks. No production database, provider HTTP acquisition, scraping, deployment, odds, training or startup migration is involved. Existing user Web launch settings are excluded from the change.

## Commands and results

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx -c Release --no-restore
dotnet test BetStats.slnx -c Release --no-build --logger 'trx;LogFileName=bs015-verified.trx'
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --configuration Release --no-build
dotnet test tests/BetStats.IntegrationTests -c Release --no-restore --filter FullyQualifiedName~Http_reads_are_safe_bounded_and_never_append_operations --logger 'trx;LogFileName=bs015-http-readonly.trx'
git diff --check
```

| Check | Result |
| --- | --- |
| Locked restore and Release build | Passed; build has zero warnings/errors |
| Unit tests | 309 passed |
| Architecture tests | 21 passed |
| Real PostgreSQL integration tests | 372 passed; zero failed/skipped |
| Full suite | **702 passed, zero failed/skipped** |
| EF model | No pending model changes |
| Fresh migrations | All 14 migrations applied by PostgreSQL fixtures |
| Upgrade from BS-014 | Previous schema populated with finalized legacy artifacts, then upgraded successfully |
| Additional HTTP canary | API startup and dashboard GET leave enabled due jobs and all pipeline records unchanged |

The complete local suite was run at the production implementation head above (integration duration 8m54s). The extra HTTP assertion was separately rebuilt and exercised in the final verification tree. Final-head CI repeats the complete suite. Local TRX files remain ignored test outputs.

## Acceptance evidence

| Area | Verification and observed behavior |
| --- | --- |
| Durable scheduling | `PipelineScheduleTests` and `PipelineSchedulerTests` cover canonical versioned definitions, idempotent planning, explicit approvals, disabled defaults, future/due boundaries, UTC-only timezone/DST policy, bounded intervals and retries. Acquisition uses PostgreSQL time and row locking. No hosted scheduler or startup dispatch is registered. |
| Multiple workers and fencing | Concurrent acquisition yields one claim. A live dedicated session owner cannot be recovered even after lease expiry. After the owner closes and the database lease expires, explicit recovery advances the attempt; stale claims cannot publish. Tampered claim clocks, definitions and owner bindings fail closed. Unique publication and immutable artifacts prevent double publication. |
| Cancellation and retries | Cancellation races return the committed Cancelled outcome, never a synthetic success. Retry budgets are enforced. Explicit recovery of an abandoned final attempt closes it as Blocked with `attempt_budget_exhausted`; another execution requires an explicit new plan. |
| Governed synchronization | `PipelineWorkflowTests` exercise missing local files, restored inputs, pending identity review, input conflicts, source revocation and retry after revocation. Existing BS-013 authorization, RAW retention, budgets and review ledgers remain authoritative. Missing transport/input becomes failed or blocked rather than successful acquisition. HTTP transport remains disabled. |
| Prediction time | Precise source-bound kickoff evidence is required. `PredictionTimePolicyTests` preserve the original calendar-day contract by default; the explicit versioned policy permits a same-day target only with precise admissible kickoff evidence. Feature evidence remains bounded by actual cutoff. Planned schedule time is separately retained and cannot replace the later actual acquisition/publication time. Missing or conflicting evidence produces exclusions or blocks publication. |
| Frozen publication and recovery | Real-clock end-to-end orchestration waits for a synthetic kickoff horizon, freezes a BS-012 prediction through BS-011 publication, injects a failure after child publication, waits for real lease expiry and recovers explicitly. Recovery reuses the single frozen child artifact; original cutoff and later execution clocks remain distinct. An interrupted unpublished prediction requires a new job instead of replay at an old cutoff. |
| Postmatch evaluation | The same end-to-end test waits past the real database kickoff boundary and evaluates only the existing frozen predictions, preserving prediction bytes. Existing v3 eligibility, independent result coverage, precise source-bound event ends and current rights are reused. Missing results/coverage/end evidence produce explicit exclusions and unavailable metrics; no completeness or accuracy certification is invented. Early evaluation is blocked. |
| Database outage and shutdown | Terminating a database backend leaves a durable unresolved Running execution without a false completion receipt. Cancellation and bounded shutdown completion are classified and owner fenced. Subsequent recovery still requires expiry, released ownership, current authorization and admissible evidence. |
| Observability | Status separates infrastructure availability from uncertified provider/data readiness and exposes durations, correlation IDs, last success and sanitized failure categories. Diagnostic tests enforce the 1,000-row bound while retaining immutable audit receipts. Completion prunes diagnostics older than 30 days; idle databases do not silently run retention work. External console collectors need their own retention configuration. No RAW bytes, tokens or restricted content enter status/log DTOs. |
| Operator interface | Explicit Development-only commands cover plan, enable, disable, run-once, bounded opt-in work, inspect, cancel, retry, recover and status. Mutations require actor, reason and approval; operator strings are audit claims rather than authenticated identities. There are no public scheduler administration endpoints. |
| Historical compatibility | `PipelineMigrationTests.Upgrade_from_bs014_and_pipeline_operation_preserve_all_finalized_legacy_artifact_bytes` freezes datasets, results and backtests for every existing football model kind on the old schema, captures canonical bytes and ledgers, upgrades and plans a job, then verifies byte identity. Existing finalized model definitions/predictions remain embedded unchanged. All legacy provenance/no-leakage tests pass. |
| Read-only API and dashboard | Existing dashboard governance, Development restrictions, Host checks, GET-only routes and legacy ledger checks pass. `DashboardTests.Http_reads_are_safe_bounded_and_never_append_operations` additionally creates an enabled due job before API startup and fingerprints jobs, versions, executions, receipts and artifacts before/after all dashboard GET routes. The fingerprint remains identical. |

## Contracts and limits

The migration adds pipeline storage and append-only audit/publication guards without modifying existing finalized data. Definition versions and execution request fingerprints bind scheduled work to its payload. Current authorization is rechecked at execution and publication; retry or recovery cannot rewrite historical rights, evidence availability or source policy bindings. Existing BS-010/011/012/013 operation identifiers remain visible through deterministic child-operation references.

The system intentionally supports approved synthetic/local files. Provider network transport needs a separate implementation, configuration and explicit authorization. A scheduler success certifies only the recorded operation outcome. It does not certify provider completeness, real-world collection, independent operational accuracy or authenticated operator identity. Metrics remain unavailable when the existing evaluator lacks eligible complete evidence.

See [ADR 0029](../../adr/0029-explicit-durable-governed-pipeline.md), the [English runbook](../../runbooks/automated-pipeline.en.md) and the [Polish runbook](../../runbooks/automated-pipeline.pl.md) for clock semantics, opt-in commands, recovery and operational limits.
