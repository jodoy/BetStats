# ADR 0030: Real football validation evidence

Status: Accepted

## Repository audit (before implementation)

Fetched main `c99e2e8c2c32c267bdfa0f4b4edd5a7593dabd3e`; GitHub confirms
BS-015 PR #19 merged on 2026-10-09. Reviewed project boundaries, ADRs 0001–0029,
BS-011–015 implementations, tests, verification reports and operator instructions.
No BS-016 workflow exists. The untracked Web launch profile belongs to the operator.

Reusable components:
- BS-013 explicit local import plans, bounded versioned parsers, RAW store/hash,
  original scope, reviewed identities, reconciliation and append-only results.
- BS-010/011 frozen feature verification, immutable publications, chronological
  evaluation, nullable metrics, exclusions and current governance checks.
- BS-012 Elo/Poisson/Dixon-Coles definitions, numerical validation and deep replay.
- BS-015 jobs, explicit dispatch, leases, ownership, cancellation, recovery and
  immutable pipeline artifacts. These are the validation operation ledger.
- BS-014 read-only dashboard already exposes model/metric/evidence projections,
  but cannot expose the new dataset-specific validation report.

Existing readiness inspects only sampled source-wide results and does not bind an
approved file, all parsed rows, prediction publications or evaluation outputs.
Import time is the present database time. CSV publication dates cannot establish
past observation, precise kickoff, inventory completeness or historical forecasts.
Governance denies unknown/revoked rights and unsatisfied retention/attribution.
No authorized real files or source rights were supplied with this task.

## Decision and implementation plan

Add an explicitly versioned, bounded validation kind to the existing pipeline.
The input binds one existing RAW capture, declared original scope/profile, an
operator evidence classification and at most three prediction/evaluation pairs.
Inspect original bytes with existing parsers, verify identities through existing
quality gates and verify referenced publications with existing model/evaluation
contracts. Do not import or execute models on report reads. Prediction and
postmatch execution remain the existing explicit BS-015 stages; validation never
creates an alternative historical execution route or alters their cutoffs.

Store the report inside the existing immutable pipeline artifact, with its exact
input fingerprint and frozen findings. Recheck current rights and RAW integrity
before publication/retrieval. A completed validation operation means the inspection
finished, not that the data/models passed. Report readiness, evidence classification,
unavailable metrics, exclusions and untested recovery separately. Operator-declared
real data is a claim, never independent proof of origin or accuracy certification.

Use a narrow Development-loopback GET projection for a report by execution UUID,
with display rights; Web consumes Application DTOs only. Existing model/quality
views remain available. Worker inspection/verification requires Development.
No new schema, dependencies, provider access, algorithms, startup work or changes
to existing immutable manifest bytes. Current rights always override stored findings.
