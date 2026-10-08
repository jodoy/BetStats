# ADR 0021: Coverage, Event Time and Evaluation Contracts

**Status:** Accepted

## Decision

Keep the modular monolith and BS-006/007 gates, RAW store and immutable snapshots.
Domain owns coverage scopes, half-open intervals, append-only assertions/reviews and
event-time claims. Application owns deterministic interval/classification/feature
gates and future evaluation/metric contracts. Infrastructure owns PostgreSQL and
explicit development Worker operations. No outcomes, models or transport are added.

Coverage dimensions include source, sport, competition, season, optional participant,
event type, observation type and an interval. UTC and calendar intervals are separate;
calendar basis must be explicit, and unknown basis cannot establish UTC coverage.
Bounds are inclusive start/exclusive end. Queries intersect at most 200 assertions;
overflow fails, never silently truncates. Gaps do not prove absent events.

Unknown means no defensible approved assertion, Partial means limited reviewed
evidence, VerifiedComplete means affirmative exhaustive evidence for the exact scope,
VerifiedEmpty means affirmative exhaustive evidence of no qualifying events,
Conflicting means contradictory overlapping strong assertions, and Expired means
only expired applicable assertions remain. Completeness never follows from ingestion,
row counts or uninterrupted dates. Strong reviews need an independently supported
basis. Initially only the documented project-owned fictional inventory contract is
machine-verifiable: permitted RAW contains the exact scope, status and exhaustive
observation inventory; it is checked against scoped observations, historical identity
decisions and the BS-006 quality gate. A supplied manual statement cannot establish
completeness. Approval states that this *fictional inventory* is exhaustive, not that
a real provider or the real world is complete. No provider guarantees are seeded.

Evidence/reviews retain RAW/hash, policy, publication/retrieval/availability, trusted
INSERT recording and explicit validity/version/approval basis. Source locks serialize
review and policy changes. Historical evidence and RAW require availability/recording
by T; interpretation/reviews/identity/quality use T or explicit retrospective R.
Later approval cannot improve HistoricalAsKnown. Current permission and expiry are
additional gates. INSERT is not COMMIT; BS-007 consistent assembly/artifacts remain
the visibility guarantee. No current canonical projection supplies historical links.

Event time records source date/time, timezone, explicit offset and justified UTC
separately from publication/retrieval/recording/prediction cutoff. DateOnly never
acquires UTC kickoff. .NET TimeZoneInfo detects DST gaps/ambiguity; ambiguity without
an explicit compatible offset yields uncertainty. Local time without context remains
uncertain. Independent contradictory claims remain visible; only explicit predecessor
corrections supersede earlier claims, never a later timestamp alone. Claims and
reviews are immutable under SQL/EF UPDATE/DELETE/TRUNCATE guards.

Feature requirements declare date/status observations, participant scope, window,
Completed status, quality v1 and partial/completeness requirements. BS-007 observed
features retain partial semantics even with complete coverage. Unknown/Partial may
qualify explicitly for observed metadata; conflicting/expired/unauthorized coverage
cannot establish completeness. Missing values never become arbitrary zeros.

Dataset definition/feature/manifest v2 freezes coverage, reviews, event-time precision,
uncertainties, gates, policies and schema versions inside the existing artifact.
Optional extensions are omitted for v1 canonical JSON, preserving exact original
hashes and original calculators. New evidence produces a new v2 artifact. Finalization
retains sorted source locks and revalidates permissions/retention; immutable v1 and
v2 artifacts remain readable and independently verifiable.

Evaluation contracts only describe future winner/goals/BTTS/first-half targets,
cutoff/horizon, required feature/time/quality/coverage evidence and separately available
labels. A label first available after prediction is forbidden as a feature but may
be eligible at a later evaluation cutoff. Corrections require new definition/version
and preserved manifests. Log loss, Brier, accuracy, calibration error and MAE specify
formats/ranges/missing labels/minimum samples/weights/aggregation; none are computed.
Current source rights are required independently of evidence integrity.

## Limits and next step

Only fictional owned inventory can support strong claims initially. Unknown timezone,
missing quality/identity or unsupported evidence fails closed. Manual operator identity
is a claim, not authentication. PostgreSQL owners can bypass triggers; runtime must
use non-owner roles (ADR 0017). No real-world completeness, scores, training, model
performance, APIs, UI or backtesting execution is claimed. BS-009 should validate one
permitted provider completeness/event-time contract and outcome provenance before
introducing an evaluation executor or prediction model.
