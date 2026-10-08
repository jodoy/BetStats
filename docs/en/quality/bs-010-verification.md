# BS-010 verification

Base: main `5678dc2b56f6b4516f36367a4af382198a853830`, after the confirmed merge
of PR #13. The branch was initially created from the then-open BS-009 head and moved
to the merged main before implementation; no pull request was merged by this task.
Local environment: Windows, .NET SDK **10.0.401**, Docker, real PostgreSQL 17 containers.
No existing local development database was migrated or seeded.

## Exact commands and final results

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger "trx;LogFileName=bs010-final.trx"
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

| Check | Result |
|---|---|
| Restore | Succeeded; all projects up to date |
| Release build | Succeeded, 0 warnings, 0 errors |
| Unit tests | 230 passed, 0 failed, 0 skipped |
| Architecture tests | 15 passed, 0 failed, 0 skipped |
| PostgreSQL/integration tests | 294 passed, 0 failed, 0 skipped |
| Total | **539 passed; all 491 BS-009 cases preserved, 48 added** |
| EF model | No changes since the last migration |

Ignored logs: `artifacts/logic-audit/bs010-final-{restore,build,test,model}.log`.
TRX: each test project's `TestResults/bs010-final.trx`.
Actual PR-head SHA and CI/CodeQL run URLs are recorded in the PR and delivery summary;
remote success is reported only after those runs finish.

## What the tests establish

- Fresh fixtures apply all eleven migrations. A separate upgrade starts at BS-009
  and preserves opaque finalized v1/v2/v3 bytes, hashes and database recording clocks.
  Existing workflow tests exercise executable v1/v2 semantics; result workflow tests
  build/verify legacy v3 and extended v3 without rewriting legacy artifact bytes.
- Independent inventory requires separately approved owned RAW, reviewed identity,
  result quality and exact scoped membership. Tests distinguish Unknown, Partial,
  Complete, Empty, Conflict and Expired; intervals, gaps, later review/reconstruction,
  original cutoffs, result corrections and explicit successor inventories are covered.
  Missing result scores and metadata-only completion cannot establish Empty/Complete.
  The unchanged-date/later-completion regression inspects current bounded status evidence.
- RAW loss/corruption and source revocation fail closed. Deep verification reports
  RAW availability/hash separately while preserving immutable snapshot integrity.
  Revoked rights deny coverage/end reads and builds before unauthorized RAW use.
- Event ends are explicit source RAW claims tied to original result scope and reviewed
  identity. Tests cover Unknown precision, explicit precise corrections, pre-correction
  history, conflicting ends, wrong original season and missing RAW. Evaluation v3
  requires a matching precise end, known clocks and independent complete result coverage;
  missing, unknown, late, wrong-result and temporally inconsistent ends are ineligible.
- One operation owns concurrent build requests. Concurrent recovery issues one replacement
  owner. Live leases and mismatched fingerprints cannot recover; expired owners cannot
  append a terminal result, even before recovery. Database checks reject missing and
  unbounded leases. Successful replay/recovery cannot reopen terminal success.
  Failed work recovers after RAW repair; cancellation after a durable claim is terminal
  and explicitly recoverable. Independent identical operations deduplicate artifacts.
- SQL UPDATE, DELETE and TRUNCATE are rejected for all four new history tables.
  New trigger/migration inventory expectations account for the additive schema.
- Operator-command integration tests enforce Development, actor/reason and explicit
  mutation approval and exercise build, status, recovery, coverage and deep verification.
  A real Worker child process runs clock-controlled demo success/reuse/success/reuse
  against disposable PostgreSQL and temporary RAW, without granting PublicDisplay.
  Clock-controlled fixture generation is deterministic across a 2032/2033 year boundary;
  the BS-009 legacy public payload/hash contract remains unchanged.

## Limits

Complete/Empty attest only the project-owned fictional inventory contract, never real
provider/world completeness. No real source transport, training, scoring, production
API expansion or UI exists. Leases last ten minutes with explicit recovery and no
automatic renewal; slow builds may outlive them. Database failure can leave Running.
Actor strings are audit claims; production authentication/role provisioning are absent.
Runtime roles must not own protected tables. PostgreSQL INSERT is not COMMIT time;
snapshots freeze committed assembly visibility. New clock scenarios use a new source
code for a different day anchor; the legacy 2026 public demo is intentionally retained.
No migration/import/recovery runs on host startup. See
[the runbook](../data/result-coverage-operations-event-end.md) and
[ADR 0024](../../adr/0024-result-coverage-operations-and-event-end.md).
