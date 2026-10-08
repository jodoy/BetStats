# ADR 0025: Historical backtesting and immutable evaluation

**Status:** Accepted

## Decision

BS-011 starts from main `0efd98f154fd05928d488cdc3fe6860d8bcb6306`, the confirmed
merge of BS-010 PR #14. Keep existing dataset bytes, schemas and calculators.
Backtests reference a verified v3 feature snapshot and freeze a separate evaluation
label read at an explicit cutoff. Predictions receive only canonical features and
identity. They never receive the label report, later coverage reviews or corrections.
Historical feature interpretation must be known at prediction T; retrospective
feature reinterpretations are rejected. Label interpretation may use explicit R.

Only versioned synthetic constant baselines are registered. Prediction records are
historical simulations: their cutoff is T, while trusted database recording time is
the actual execution time. Neither is backdated. Feature hashes exclude labels.
Evaluation v3 uses the existing eligibility contract, independently reviewed complete
result coverage and precise, source-bound event-end evidence. Missing/conflicting
evidence yields exclusions, not zero outcomes or invented completeness.

Store canonical immutable manifests/reports/predictions together in a separate
append-only backtest artifact. Reuse BS-010 operation states, request fingerprints,
DB-clock leases, explicit recovery and owner-token fencing in a dedicated ledger.
Publication and successful terminal append are atomic under sorted source locks.
Every replay/read rechecks current authorization; deep verification checks original
RAW. No automatic execution, startup migration or administrative HTTP route.

Metrics v1 use ordered samples, decimal probabilities summing exactly to one,
unweighted denominators, explicit null for empty samples and explicit positive
infinity for zero true-class probability. Logarithms use .NET Math.Log rounded to
12 decimal places. Binary Brier uses both classes (range 0..2), matching ADR 0021.
Accuracy ties choose the first declared class. Calibration uses ten equal-width
bins, including probability one in the last bin. Reports expose the existing
100-sample threshold independently of descriptive values; a tiny fixture is not
validated model performance. Count predictions use MAE, never categorical metrics.

## Limits

Existing v3 targets require prediction before the UTC calendar day. A newly captured
fixture cannot honestly acquire pre-event recording history. Such a backtest must
report ineligible rows until real permitted history exists; tests may exercise pure
frozen historical scenarios without pretending they were recorded in PostgreSQL.
Only project-owned fictional completeness contracts exist. Operator names are
claims, not authentication; runtime PostgreSQL roles must be non-owner.
No training, real provider transport, odds, production scoring or UI is introduced.
