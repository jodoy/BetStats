# BS-012 verification

Base: main `352d920c66fb4608eb5b77100ed1f9277af626f6`, confirmed merged BS-011
PR #15. Its actual head `f3281d8f3ca4d3d0a3be383f5f35eacdb4c0edc4` passed
[CI #39](https://github.com/jodoy/BetStats/actions/runs/37861278770) and
[CodeQL #37](https://github.com/jodoy/BetStats/actions/runs/37861278746), including
SARIF processing. No unmerged predecessor dependency remains.
Branch: `feat/bs-012-football-models`. No pull request is merged by this task.

Environment: Windows, .NET SDK **10.0.401**, Docker, isolated **PostgreSQL 17**
containers. Existing user development databases were not migrated, seeded or stopped.
No real provider requests, credentials, odds or restricted datasets were introduced.

## Exact verification commands

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger "trx;LogFileName=bs012-delivery.trx"
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
```

The final delivery run (`bs012-delivery.trx`) passed all 622 tests, exit 0,
including model forecasts bound through the prediction port. PostgreSQL tests
took 4 minutes 17 seconds. No commands listed here are reported as successful
without an actual successful exit code.

| Check | Final local result |
|---|---|
| Restore | All projects current, exit 0 |
| Release build | 0 warnings, 0 errors, exit 0 |
| Unit | 280 passed, 0 failed, 0 skipped |
| Architecture | 19 passed, 0 failed, 0 skipped |
| Real PostgreSQL/integration | 323 passed, 0 failed, 0 skipped |
| Total | 622 passed; all 585 BS-011 cases retained, 37 added |
| EF | No model changes since the last migration; exit 0 |
| Fresh PostgreSQL 17 | All 12 existing migrations applied, no pending migrations/model changes |
| BS-011 compatibility | Existing schema reused; finalized legacy backtest content/hash/clocks/ledger and datasets remain unchanged after all model variants |

Ignored logs: `artifacts/logic-audit/bs012-final-{restore,build,model}.log` and
`bs012-delivery-test.log`. TRX is in each test project's TestResults directory.
The final commit SHA and actual-head CI/CodeQL URLs/outcomes are recorded in the
PR description and delivery summary after remote runs finish. Remote checks are
not inferred from local tests. CI/CodeQL workflows remain unchanged.

## Tested behavior and compatibility

- Elo expected score, equal-rating updates, draw, zero-sum paired updates,
  same-day batch order invariance and explicit season reset/retention.
- Poisson analytic zero score, BTTS, O/U2.5 and expected goals, coherent exact-score
  and total distributions, exact decimal mass, tail-bound rejection, bounded rates.
- Dixon-Coles zero identity, nonzero low-score effects, negative/zero factor rejection.
- Explicit prior/warm-up states, independently missing half-time history; future
  availability/recording, same-day/target outcomes and duplicate versions rejected.
- Deterministic replay and parameter/input/correction hashes. Positive fictional
  historical unit scenarios evaluate all six targets, with small-sample disclosure.
  Equivalent-event comparisons exclude unwarmed models from the common sample set.
- Current source permission, RAW absence/corruption, parallel publication,
  fingerprints, cancellation, live/stale lease fencing, retries/recovery and
  finalized replay, tested for both synthetic BS-011 and model backtests on PG17.
- Real Worker child processes exercise model plan/backtest/report/deep verification
  and finalized recovery. Operator inspection/simulation is read-only and denies
  non-Development execution; mutations retain actor/reason/approval requirements.
- Legacy canonical definition/input shapes match their original fields exactly.
  Null model/forecast/fold extensions are omitted. Dataset serializers/calculators,
  published migrations, dependency references and metric v1 implementation are untouched.
  Existing migration tests preserve v1/v2/v3 opaque bytes and hashes across earlier
  upgrades; existing workflow tests exercise executable v1/v2/v3 artifacts.
  A PG17 BS-011 database is populated with a finalized legacy backtest before model
  operations; its exact content/hash/recording clocks and operation history are
  checked afterwards, including deep legacy verification. BS-012 needs no schema
  upgrade: its baseline/latest migration is `20261008231727_HistoricalBacktesting`.

## Remaining limitations

**Zero eligible operational samples** in newly recorded fictional PostgreSQL
scenarios. Positive unit samples are explicitly synthetic, not real historical
recording evidence. No empirical model accuracy, improved performance or calibration
is claimed. Optional calibrated ensemble is **not implemented/enabled** pending
a versioned, authorized calibration artifact known before prediction. This is a
disclosed remaining optional capability, not a calibrated identity transform.

Elo replays a 30-day observed-history window in the existing competition/season
scope, not lifetime/cross-season history unavailable from that snapshot. Its goal
bridge and the prior-smoothed Poisson rates are declared baseline assumptions,
not fitted regression. Parameters remain fixed throughout chronological evaluation.
Math function/rounding version is documented; arbitrary-runtime bit identity is
not promised. Artifact/request limits are 16 MiB/1 MiB; large snapshots may fail
without publication. Grids are frozen once per event/cutoff with hash references.

Existing operator claims are not authentication. Use non-owner runtime PG roles.
DB owners can bypass triggers. Leases have no automatic heartbeat/retry; explicit
recovery is required after interruption/expiry. RAW may disappear after assembly;
deep verification reports this separately from immutable content integrity.
No production scoring/API/UI, automatic startup migration/import/evaluation or merge.

See [ADR 0026](../../adr/0026-football-prediction-models.md),
[English runbook](../data/football-models.md) and
[Polish runbook](../../pl/data/football-models.md).
