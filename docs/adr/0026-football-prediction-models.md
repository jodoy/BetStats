# ADR 0026: Bounded historical football models v1

**Status:** Accepted

## Decision

Start from main `352d920c66fb4608eb5b77100ed1f9277af626f6`, the merged BS-011
PR #15. Its actual head CI and CodeQL succeeded. No dependent unmerged PR remains.

Keep the modular monolith and all dataset calculators/serializers unchanged.
Backtest definition v2 adds an immutable explicit model definition; optional null
extensions are omitted from legacy JSON. Canonical hashes bind definitions, inputs
and provenance. Existing definition v1 continues to use the synthetic baseline.
Model artifacts use the existing append-only backtest store and fenced recovery
ledger; no database schema change or startup work is necessary.

Model inputs contain reviewed canonical finished regulation results from the same
30-day observed-history window as dataset v3, known on both clocks at prediction T.
Rebuild each cutoff independently; never carry a later state backwards. Elo replays
calendar days in batches, using pre-day ratings for simultaneous matches. It is a
bounded-window rating, not a lifetime rating. Explicit base ratings and season
retention/reset parameters define initialization; no missing history is invented.

Poisson rates use explicit prior pseudo-counts and observed team attack/opponent
defence means. The Elo goal bridge is an explicit synthetic modelling assumption,
not a fitted conversion of rating points. A single bounded joint-score grid gives
all full-time markets and expected totals. Validate discarded mass before conditional
normalization, apply an optional versioned Dixon-Coles low-score correction only
when all four factors are positive, and quantize with deterministic residual repair.
First-half markets use only independently observed paired first-half history and
their own warm-up gate; never halve full-time goals.

Unwarmed predictions are explicitly prior simulations and excluded from metrics.
Walk-forward execution orders prediction cutoffs; immutable per-cutoff replays prevent
random split leakage. Evaluation still requires existing authorization, RAW integrity,
complete independent result coverage and precise source-bound event end. Comparisons
recompute metrics on the intersection of eligible events for every target.

## Limits

No model fitting or empirical calibration is claimed. Optional ensemble is deferred
until an eligible calibration artifact exists. No real provider, credentials, odds,
production scoring route/UI, migration or import is introduced. Newly recorded
fictional evidence cannot establish pre-event history for past events. Operational
reports can honestly contain zero eligible samples; pure mathematical fixtures are
not operational historical evidence. IEEE Math functions rounded to decimal form
are the versioned numeric contract, not a promise across arbitrary runtimes.
