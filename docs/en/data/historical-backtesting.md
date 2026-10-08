# Historical backtesting (BS-011)

This is a Development operator engine for **historical simulations**, with a fixed
synthetic baseline. It is not production scoring or a trained model. ADR 0025
starts at merged BS-010 main `0efd98f154fd05928d488cdc3fe6860d8bcb6306` (PR #14).
No predecessor PR dependency remains. No PR is merged by this task.

## Evidence and time

Use an existing, independently verified result dataset v3. Legacy metadata v1/v2
and result v3 artifacts retain their original exact bytes and hashes. Backtests are
new artifacts in `evaluation.Backtests`; their predictions and reports are frozen
together. Each prediction stores event identity, T, predictor/version, dataset ID
and hash, **label-independent feature/input hashes**, decimal probabilities or an
expected count, and evidence IDs. Dataset hash is a reference to the existing v3
artifact, which may contain later labels; those labels never enter predictor input.
The trusted database artifact/operation recording clock is actual execution time,
not T. Existing historical datasets are never rewritten to simulate earlier capture.

Features, original RAW, identity, quality and policy interpretation must be known
at prediction T. Retrospective feature interpretation and a target outcome already
known at prediction fail the operation. Only canonical `PredictionInput` reaches
the provider; it cannot carry labels, RAW/provider DTOs or evaluation reviews.
Current feature-dataset integrity, reproducibility, authorization and RAW are
checked before planning. No missing input value becomes an observed zero.

Label observations are read separately at `EvaluationCutoffUtc` under REPEATABLE
READ. All targets share one explicit HistoricalAsKnown T or retrospective R label
interpretation. R is bounded by evaluation; feature interpretation stays historical.
Evaluation definition versions >=3 preserve v3 eligibility and explicitly admit
correction versions up to the definition version. New cutoffs/definitions create new
manifests, not changes to predictions or prior reports. Results must be unique,
eligible finished regulation-time observations for the reviewed event/participants.
Conflicts, missing first-half pairs and a result date differing from the frozen
prediction target date yield exclusions. Rescheduled dates cannot borrow complete
coverage from the original calendar day; reassessment needs a justified new snapshot.

Evaluation uses BS-008/009/010 eligibility: complete metadata feature coverage,
independent **Complete result coverage for the target calendar day**, source rights,
prediction horizon, outcome availability after prediction, both outcome clocks by
evaluation, and a matching precise source-bound event end. Kickoff/end are never
estimated. Unknown, Partial, Empty, Conflict and Expired result coverage are not
Complete. Metadata completion cannot establish result completeness.

Fresh demo capture usually has no pre-event recording history, outcome, approved
full coverage or event end for its future target. A successful run can therefore
publish a fully explained report with **zero eligible samples and null metrics**.
That is a valid operational result, not measured predictive performance. Positive
historical evaluation fixtures are pure clock-controlled scenarios, not backdated
PostgreSQL evidence or real provider completeness.

## Targets and metric v1 semantics

`synthetic-constant` v1 supports MatchWinner (H/D/A probabilities .4/.3/.3),
BothTeamsScoring, OverUnder25 and FirstHalfGoalOccurrence (false/true .5/.5),
TotalGoals (expected regulation count 2), and FirstHalfTotalGoals (expected count 1).
These priors are declared fictional constants; no historical rate is estimated.
First-half targets require justified regulation-time paired half-time scores on an
eligible finished result. Extra-time/penalty totals are excluded.

- Probabilities are finite decimals in [0,1], with exact sum 1 and exactly two/three
  classes. Counts are finite decimals in [0, int.MaxValue], without probabilities.
  Unsupported shapes, normalization and mixed formats are rejected.
- Accuracy uses the first class in declared order to break ties.
- Brier is the mean **sum** of squared class errors. Both binary classes are used;
  its range is [0,2], matching the existing metric contract.
- Log Loss uses natural log, no epsilon/clipping. A zero true-class probability
  yields `PositiveInfinity=true, Value=null`. Finite per-sample logs use .NET Math.Log
  rounded to 12 decimal places, midpoint-to-even; aggregation uses decimals.
- MAE is the mean absolute error for justified nonnegative integer counts.
- Calibration tables have ten equal-width bins per class (one-vs-rest for 1X2).
  Probability 1 belongs to bin 9. Empty bins have count 0 and null averages.
  Binary calibration_error is the sample-weighted absolute bin gap.
- Samples are ordered by event ID, equal weight, one per event and target. Every
  metric denominator is **eligible labelled samples**, not requested events. Empty
  samples yield null values (never zero); exclusions and requested/eligible counts
  are reported per target. Reasons can overlap and must not be summed as a total.
- The existing 100-sample minimum is exposed as `MinimumSamplesMet`. Descriptive
  values below it are not a claim of validated model performance. No confidence
  intervals, significance tests or arbitrary user weighting are implemented.

## Operator commands

Build Release first. Explicitly migrate only your chosen disposable development DB
using the existing persistence runbook; hosts never migrate/import/evaluate at startup.
Configure `ConnectionStrings__BetStats` and `Ingestion__RawStoragePath` locally,
with no credentials or RAW files committed. Use non-owner runtime PostgreSQL roles.

`BacktestDefinition` JSON uses canonical UTC microseconds and exact metric records
from `EvaluationContracts.Metrics`. Obtain DatasetId/ExpectedDatasetHash/SportId from
`Results:Action=inspect-v3`, never guessed values. Example definition shape (replace
the three placeholders with verified evidence):

```json
{
  "Version": 1,
  "DatasetId": "<verified-v3-uuid>",
  "ExpectedDatasetHash": "<verified-64-character-sha256>",
  "Predictor": "synthetic-constant",
  "PredictorVersion": 1,
  "EvaluationCutoffUtc": "<actual-UTC-cutoff-with-six-fractional-digits>Z",
  "Evaluations": [{
    "Version": 3,
    "SportId": "<dataset-sport-uuid>",
    "Target": "TotalGoals",
    "Mode": "HistoricalAsKnown",
    "ReconstructionUtc": null,
    "CutoffPolicy": "BeforeCalendarDay",
    "PredictionHorizon": "01:00:00",
    "RequiredEvidence": ["features", "event-time", "quality", "coverage", "source-policy"],
    "OutcomeObservationType": "TotalGoals",
    "QualityVersion": 1,
    "Coverage": {
      "FeatureName": "result", "Version": 1, "ObservationTypes": ["EventDate"],
      "LookbackDays": 30, "ParticipantRequired": false, "RequiredStatus": "Completed",
      "MinimumQualityVersion": 1, "PartialAllowed": false, "CompletenessRequired": true
    },
    "Metrics": [{
      "Name": "mae", "Version": 1,
      "PredictionFormat": "finite nonnegative expected-count",
      "LabelFormat": "nonnegative integer count", "ValidRange": "error >=0",
      "MissingLabelBehavior": "exclude-and-report", "MinimumSamples": 100,
      "Weighting": "nonnegative finite weights; positive sum",
      "Aggregation": "global weighted absolute error"
    }],
    "OutcomeAvailabilityRule": "after-prediction-and-recorded-by-evaluation-v1",
    "HorizonRule": "minimum-lead-time-v2"
  }]
}
```

Do not execute placeholder JSON. Add other targets with their matching metric
catalog records; counts use only MAE, MatchWinner cannot use binary calibration_error.
Both prediction cutoff policies require existing justified event evidence; precise
kickoff policy cannot manufacture kickoff from a calendar date.

PowerShell command pattern:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Backtest__DefinitionJson = Get-Content -Raw ./local-backtest-definition.json
$env:Backtest__OperatorId = 'operator:local'
$env:Backtest__Reason = 'Review permitted fictional historical simulation'
dotnet src/BetStats.Worker/bin/Release/net10.0/BetStats.Worker.dll --Backtest:Action=plan
```

`plan` is read-only. Save its reviewed definition; then explicitly set a new
operation UUID (`Backtest__OperationId`) and approval (`Backtest__Approve=true`)
before `--Backtest:Action=run`. `operation` reads durable status by OperationId.
`inspect`, `verify` and `verify-deep` require `Backtest__SnapshotId`. Deep verification
reports original RAW availability/hash separately from immutable artifact integrity.
Any denied current authorization prevents evidence/RAW reads. Verification returns
nonzero exit status on failed integrity/reproduction/authorization or deep RAW checks.
Planning and successful execution with exclusions are exit 0; failed/running/cancelled
operations are exit 1. These outcomes do not certify eligibility or performance.

## Recovery and limits

`recover` requires OperationId, `Backtest__ExpectedFingerprint` from the ledger,
explicit actor/reason and approval. It reuses the exact durable request. A live owner
cannot be replaced. Interrupted expired leases, Failed and Cancelled can acquire a
fresh owner; Succeeded returns the existing authorized artifact. Changing a definition
under an operation UUID fails. Use a new UUID/definition for intentional changes.

The ledger reuses BS-010 states, fingerprinting, DB-clock bounded leases and shared
ownership fencing. Artifact publication and Succeeded commit in one transaction.
Concurrent identical content is deduplicated; stale owners cannot publish. Current
source locks serialize revocation with publication. Manifests freeze current policy
IDs/versions/audit IDs as well as historical feature/label policy provenance. A policy
change during assembly requires re-planning; subsequent reads recheck current rights.
DB failure may leave Running and prevent a failure append. Leases have no heartbeat;
an operation exceeding the default ten minutes requires explicit recovery. No automatic
retry, scheduling, recovery, migration or import occurs.

Limits: project-owned fictional inventory only; operator identifiers are claims, not
authentication. Database owners can bypass history triggers. Filesystem RAW can be
removed after assembly; deep verification exposes that loss without changing history.
Positive performance on real permitted historical evidence is not verified or claimed.
No provider HTTP, credentials, bookmaker odds, training, production API/UI or deployment.
