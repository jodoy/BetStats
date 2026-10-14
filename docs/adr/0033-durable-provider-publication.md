# ADR 0033: Durable provider publication

Status: Proposed; runtime publication is not enabled by this document.

## Context

See the [pre-implementation audit](../en/quality/bs-019a-audit.md). BS-018 provides
strict API-Football parsing and original content, but deliberately blocks canonical
JSON publication. CSV receipt and recovery contracts cannot certify JSON publication.

## Decision

Reuse canonical entities, provider identities and versioned review decisions,
observation/result correction chains, RAW manifests, source governance and BS-015
jobs. Do not convert JSON to CSV, infer identity from names or create a second pipeline.

Introduce a versioned source-to-domain mapping boundary that consumes the existing
strict reader. Keep provider IDs and statuses in source provenance. Require exact
league/season and source/profile context. Metadata publication and final-result
eligibility are independent: postponement, cancellation and live status are metadata;
only supported final regulation-time scores become final-result observations.

The eventual transactional publisher must pin original RAW hash/clocks/scope,
policy UUID/version, all identity decision versions and mapping version. It must commit
canonical observations and an immutable publication receipt atomically under current
source rights and the BS-015 owner fence. Replay reuses that receipt. Recovery reads
the original RAW; it never retrieves or recaptures provider data.

Use existing `ScheduledStart` observations and `EventTimeEvidence` for precise time.
Keep occurrence, source publication, retrieval, availability and database recording
times distinct. A missing source publication timestamp remains null. Frozen artifacts
are never rewritten; future kickoff alone establishes no prospective eligibility.

## Consequences

No runtime JSON profile registration is permitted until transactional receipts,
governance, recovery, concurrency and PostgreSQL tests exist. This ADR does not claim
those components have been delivered. Live HTTP remains out of scope. Publication
monitoring must remain read-only and respect separate display rights.
