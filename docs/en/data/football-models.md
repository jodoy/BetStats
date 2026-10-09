# Football prediction models v1 (BS-012)

Development-only mathematical models and historical simulations. No fitted model,
real-world accuracy claim, live provider, odds, production scoring API or UI.
Start: merged BS-011 PR #15, main `352d920c66fb4608eb5b77100ed1f9277af626f6`.
See [ADR 0026](../../adr/0026-football-prediction-models.md) and the
[BS-011 authorization, recovery and metric contracts](historical-backtesting.md).

## Inputs and chronological boundaries

Backtest definition **v2** contains `Model`, an immutable `FootballModelDefinition`
v1, and uses `football-elo`, `football-poisson` or `football-dixon-coles` predictor
v1. The model, explicit hyperparameters, feature dataset hash, feature hash,
canonical input hash and original evidence references are frozen in the prediction.
Full/half score distributions are frozen once per event/cutoff in ModelForecasts;
each market prediction binds their hashes rather than duplicating large grids.
Definitions v1 and old dataset/backtest JSON omit all new null properties. No
serializer, legacy calculator, dataset version or migration changes.

Input v2 adds canonical reviewed finished regulation-time results to input v1.
Only prior UTC calendar days within the existing **30-day observed history** are
used; both availability and recording, identity/quality/policy review clocks must
be <= prediction T. Duplicate event versions/conflicts fail. The dataset adapter
already selects the reviewed as-of correction; a later correction cannot change
a frozen input. No provider DTO, target outcome or evaluation label reaches a model.

Walk-forward folds group equal prediction cutoffs, ordered ascending. Each fold
independently replays its frozen history. Hyperparameters remain fixed; no fitting,
random split, future-state reuse or retrospective feature interpretation. Observed
history is partial, not certified complete. This is a bounded-window baseline;
neither season-wide nor lifetime ratings are claimed. The current v3 snapshot
scope restricts results to its competition/season. Cross-season state outside that
scope/window is unavailable and cannot be fabricated.

`MinimumMatches` (default 3) requires that many observed games for **each** team.
`MinimumHalfMatches` (default 3) separately requires paired first-half history.
Without warm-up the prediction is explicitly an unvalidated prior simulation,
with `Warmed/HalfWarmed=false`; relevant samples are excluded from metrics.
Missing history never becomes an observed zero. Invalid rates fail the operation.

## Mathematics and numeric version

Elo: `E = 1/(1+10^((away-home-homeAdvantage)/scale))`,
`delta = K*(actual-E)`, actual = 1/.5/0. Parameters include base rating (1500),
home advantage (60), K (20), scale (400), and season retention [0,1] (default 1).
All games on one calendar day use the pre-day ratings and accumulate updates;
no arbitrary event ordering creates an advantage. Season changes regress toward
base using retention, including the target season boundary. Updates use only
finished history known at T; there is no guessed event-end timestamp.
Rating deltas round to 12 decimal places, expected score to 15.

The Elo score bridge is an explicit **synthetic assumption**:
`lambdaH = prior*10^((ratingH+homeAdvantage-ratingA)/GoalBridgeScale)`;
`lambdaA = prior / multiplier`. Default bridge scale is 800. It is not a fitted
football interpretation of Elo expected scores; `HomeMultiplier` applies to the
Poisson estimator, while Elo uses its explicit home advantage.

Poisson: estimate each team's attack and opposing defence by observed means with
explicit prior pseudo-counts, then average these rates. Default full-time prior
is 1.25 goals per side, prior weight 2 matches, home multiplier 1.1.
Half-time uses its **own** prior 0.55 and independently observed first-half counts,
not half the full-time estimate. Elo's half-time bridge likewise uses the explicit
half prior and requires first-half warm-up for evaluation.

`P(i,j)=exp(-lambdaH)*lambdaH^i/i! * exp(-lambdaA)*lambdaA^j/j!`.
Rates must be >0 and <= MaximumRate (default 6, maximum configurable 10); never
clip. Grid includes goals 0..MaximumGoals (default 30; configurable 3..60).
Reject if omitted joint mass exceeds MaximumDiscardedMass (default 1e-6).
The returned grid is **conditional on this bounded support**, normalized after
validation. Publish discarded mass explicitly. Recurrence uses IEEE .NET Math;
cells quantize to decimal 15 places, midpoint-to-even. Repair residual into the
largest cell, ties in home/away grid order, then verify exact mass 1 and [0,1].
All 1X2, BTTS, O/U2.5, expected goals, goal totals and exact scores derive from
that same grid. Count predictions use the expected count of the truncated grid,
not an inconsistent untruncated lambda sum. Half-time uses a separate grid.

Optional Dixon-Coles uses a distinct model kind/version and explicit rho:
00: `1-lambdaH*lambdaA*rho`, 01: `1+lambdaH*rho`,
10: `1+lambdaA*rho`, 11: `1-rho`, all others 1.
All four factors must be strictly positive at actual rates; otherwise fail.
Rho=0 is byte-identical to the independent Poisson grid. These are fixed
parameters, not fitted Dixon-Coles regression/time decay. Reference:
[Dixon and Coles (1997)](https://rss.onlinelibrary.wiley.com/doi/10.1111/1467-9876.00065).

Optional ensemble remains disabled/deferred: there is no verified historical
calibration artifact. Identity priors are not described as calibrated, and no
accuracy improvement is claimed. Adding a calibrated ensemble needs a separately
versioned, permission-checked calibration artifact known before each prediction.

## Evaluation and comparisons

BS-011 still enforces source permission, original RAW integrity, independent
complete result coverage, precise source-bound event end, horizon and outcome
availability. Models do not create coverage/end evidence. Each target report gives
requested/eligible/excluded counts, reasons, Accuracy, Brier, Log Loss or MAE and
calibration tables. Empty values are null; fewer than 100 eligible samples fail
the existing minimum-sample flag. All metric v1 definitions remain unchanged.

`compare` deeply verifies every stored backtest and current rights. Dataset,
evaluation cutoff, label evidence and metric definitions must match. Metrics are
recomputed on the **intersection of eligible event IDs per target**, with explicit
IDs and denominators. Different warm-up exclusions cannot bias comparisons by
silently using different event sets. Zero intersection means zero denominators
and null metrics, not comparative performance.

## Explicit Worker operations

Build Release. Set `DOTNET_ENVIRONMENT=Development`. Use only your own explicit
development connection/RAW path; hosts never migrate/import/evaluate on startup.
Configure locally `ConnectionStrings__BetStats`, `Ingestion__RawStoragePath`.
Do not commit connection secrets, RAW or copied operational datasets.

All commands require `Model:OperatorId` and `Model:Reason` (operator claims, not
authentication). Mutation requires `Model:Approve=true`, explicit OperationId
and the BS-010/011 fenced ledger. Environment variables use double underscores.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Model__OperatorId = 'operator:local'
$env:Model__Reason = 'Explicit mathematical model inspection'
$env:Model__Action = 'inspect'
$env:Model__DefinitionJson = '{"Version":1,"Kind":"Poisson","Elo":{},"Goals":{},"MinimumMatches":3,"MinimumHalfMatches":3,"HistoryDays":30}'
dotnet run --project src/BetStats.Worker --configuration Release --no-build
```

`inspect` prints definition/hash/requirements; `simulate` additionally requires
`Model:InputJson` containing canonical input v2 with explicit clocks/history and
prints an **unverified mathematical simulation**, not persisted operational evidence.
No database connection or provider transport is used for inspect/simulate.

For `plan`/`backtest`, `DefinitionJson` is instead a full BacktestDefinition v2
copied from the BS-011 runbook, with `Predictor=football-poisson`, `PredictorVersion=1`
and `Model` above. Obtain actual DatasetId/hash/SportId/metric records from a
verified v3 snapshot; never invent them. Other actions: `operation` with OperationId,
`report`/`verify`/`verify-deep` with SnapshotId, `recover` with OperationId and exact
ExpectedFingerprint, `compare` with comma-separated SnapshotIds (2..10).
Backtest/recover require approval. They delegate to BS-011 publication, current
authorization locks, idempotency and ownership fencing. Existing backtest JSON
output keeps action names `run`/`inspect` for these delegated aliases.

Artifacts remain bounded to 16 MiB, requests to 1 MiB. Large grids/many targets can
exceed this limit and fail without publication; reduce snapshot size or grid size
only when the declared tail bound still passes. Interrupted operations need
explicit recovery after their trusted DB-clock lease expires; no automatic retry
or stale owner can publish. No new migration is needed: BS-011's 12 migrations
already protect immutable content and ledger requests.

Fresh fictional imports yield **zero eligible operational evaluation samples**
when historical outcomes/coverage/end evidence are absent. Positive mathematical
unit fixtures are clock-controlled fiction, never backdated database observations.
See [verification report](../quality/bs-012-verification.md).
