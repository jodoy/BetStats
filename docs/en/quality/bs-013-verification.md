# BS-013 verification

Baseline: `origin/main` eab5c4867e58eae3183a944ddbc3a670b3f79b47,
confirmed merged PR #16 (BS-012). Branch: feat/bs-013-real-football-data.
No pull request is merged by this task.

Environment: Windows, .NET SDK 10.0.401, Docker 29.6.1, isolated disposable
PostgreSQL 17 Alpine containers. No user database migration, source authorization,
provider request or real dataset import was performed. Existing untracked Web
launch settings belong to the user and are excluded from delivery.

## Exact commands

```powershell
git fetch origin
git log origin/main -2 --oneline
git switch -c feat/bs-013-real-football-data origin/main
dotnet restore BetStats.slnx
dotnet build BetStats.slnx -c Release --no-restore
dotnet tool restore
dotnet ef migrations add RealFootballImportOperations --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --configuration Release
dotnet test BetStats.slnx -c Release --no-build --logger 'trx;LogFilePrefix=bs013-delivery' --results-directory TestResults/BS013Delivery
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure --configuration Release --no-build
git diff --check
```

The final local delivery run passed **648 tests**, with **0 failed and 0 skipped**.
PostgreSQL integration tests took 5 minutes 12 seconds. All 622 BS-012 cases
remain, with 26 added cases. Initial implementation runs exposed and resolved
schema-count expectations, a test project boundary and query translation, and
missing sport scope for unreviewed identities; those runs are not delivery evidence.

| Check | Final local result |
| --- | --- |
| Restore | All projects current, exit 0 |
| Release build | 0 warnings, 0 errors, exit 0 |
| Unit | 286 passed |
| Architecture | 20 passed |
| PostgreSQL 17/integration | 342 passed |
| Total | 648 passed, 0 failed, 0 skipped |
| EF model check | No changes since last migration, exit 0 |
| Fresh database | All 13 migrations applied, no pending model changes |
| BS-012 upgrade and import | Finalized dataset/model/prediction/backtest bytes, hashes, clocks and history preserved; deep verification passed |
| Diff check | No whitespace errors |

Remote CI/CodeQL outcomes are added after the PR's actual head is checked.

## Evidence and limits

The additive migration creates only the FootballImportOperations journal, indexes
and trusted-clock/owner/append-only triggers. Existing published migrations and
artifact serializers/calculators remain unchanged. PostgreSQL tests cover a fresh
database and an actual BS-012 schema upgrade with finalized Poisson model/backtest
bytes, model definitions, frozen predictions, dataset bytes/hashes/clocks and
operation history preserved and deeply reverified. Existing v1/v2/v3 migration
and executable regression cases remain in the complete suite.

New cases cover supported/invalid CSV, null scores, scope/time errors, duplicate
and conflicting events, explicit identity review, reconciliation, late correction,
policy revocation before file open, source lock contention, changed-file rejection,
RAW corruption, bounded pagination, retry categories and actual Worker commands.
Existing provider governance cases exercise request budgets, timeouts, 429 cooldown,
concurrency and retention/attribution restrictions; existing staged recovery and
ownership tests remain required. No test substitutes an in-memory database for PG17.

No authorized real-provider fixture or API grant/configuration was supplied. The
local-file workflow is verified using project-authored fictional CSV bytes only;
successful live ingestion is not claimed. API transport remains an interface with
bounded pagination and the existing authorized executor, not a registered HTTP
implementation. Real-file execution and live transport remain blocked on explicit
source permission and an authorized operator-supplied fixture/configuration.

Readiness is a bounded report of observed history, not a training/evaluation result.
It reports unknown fixtures without independent reviewed inventory, keeps metadata
and result coverage separate, reports RAW sample integrity and certifies zero
usable feature windows until BS-011 evidence gates are satisfied. No accuracy,
calibration, full inventory or pre-event recording claim is fabricated.

The CSV profile accepts a bounded documented projection, not arbitrary provider
exports. It cannot infer kickoff timezone or unavailable historical publication
times. Retention caps with no supported plan fail closed. Source locks and database
owner checks fence capture/publication; leases have no automatic heartbeat, and
recovery is an explicit operator action. Operator identifiers are audit claims,
not authentication. No scheduling, startup imports, scraping, odds, production
scoring/API/frontend or automatic merges are introduced.
