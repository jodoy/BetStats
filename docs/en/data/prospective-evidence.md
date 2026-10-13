# BS-017 prospective evidence operator guide

Origin and timing are independent dimensions. `Synthetic` is project-owned software
test data. `OperatorDeclaredReal` is an operator assertion of real origin; `Unknown`
asserts neither. A receipt may be historical even for a real source. Independently
verified provider origin/completeness remains unavailable without a concrete
supported source contract. Successful parsing/HTTP status is never inventory proof.

## Observe before predicting

Capture reviewed upcoming fixtures with their actual present acquisition clocks.
Apply existing identity review and reconciliation when needed. Acquire precise
kickoff evidence separately using the existing `EventTimeSourceClaim` v1 shape.
The acquisition report preserves source input precision/local date/time/offset,
resolved UTC kickoff, RAW receipt and database recording times. Metadata source
publication and result publication/availability/recording are distinct fields;
unknown times remain null. File modification time is not trusted receipt evidence.

A prospective receipt requires reviewed canonical identity, distinct home/away,
precise unambiguous kickoff after cutoff, and metadata and kickoff both available
and database-recorded before cutoff. Historical imports cannot meet an earlier
cutoff. `PreEventObserved` describes necessary recording evidence, not a prediction
eligibility certificate. Readiness reads do not run predictors or build features.

Explicitly invoke the existing approved BS-015 `PrematchPrediction` workflow.
Its actual database acquisition time, source-bound kickoff/horizon and historical
feature gates remain authoritative. Elo, Poisson and Dixon-Coles are reused without
new algorithms or training. Warm-up priors remain labeled simulations; a frozen
prior is not counted as an eligible prediction in the prospective summary.

## Reconcile and evaluate

Capture independently observed postmatch result files as `PostmatchResults`.
Use existing BS-013/009 reconciliation, independently justified result inventories
and event ends, and BS-015 `PostmatchEvaluation`. A score with unsupported status,
basis, conflicting identity or incomplete evidence is not automatically final.
Corrections append new observations with links and real recording clocks. They
never rewrite the original prediction or move future knowledge into its features.

Use BS-016 validation to bind original RAW, frozen prediction and optional frozen
evaluation. Its reproducibility and eligibility gates remain intact. Freeze a
`ProspectiveCohort` referencing at most 10 acquisition executions, three validation
executions and two exact-source coverage queries. The cohort records input hash,
source, assessment time and immutable child hashes. It deduplicates model execution
pairs and event identities rather than multiplying repeated receipts.

Coverage queries reuse existing contracts; the actual cutoff is the cohort's
database assessment time. Scope/category/interval must match the acquired source.
Owned-fixture completeness is synthetic evidence only. Real/operator-declared
cohorts conservatively remain Unknown until an independently supported real-source
inventory contract exists. Manual completeness assertions cannot pass strong
coverage review. Frozen coverage records retain exact evidence/review IDs, RAW
hashes and validity; expired/corrupt evidence blocks subsequent reads.

Counts distinguish observed events, pre-event receipts, distinct frozen prediction
events, warmed eligible prediction events, completed result labels and eligible
target/model evaluation samples. The latter is a sum of preserved target sample
counts, not a distinct-match count or success percentage. Full BS-016 target
reports retain original denominators, nulls, calibration and exclusions. Models
are not ranked across different samples. Small cohorts imply no statistical
significance or operational accuracy certification.

Reports are immutable snapshots. Later acquisition/reconciliation requires a new
explicit cohort to report new observations. Reads recheck current rights, RAW and
validity without reexecuting acquisition or metrics. See the
[English](../../runbooks/authorized-acquisition.en.md) and
[Polish](../../runbooks/authorized-acquisition.pl.md) runbooks for exact commands.
