# ADR 0023: Football Results and Outcome Provenance

**Status:** Accepted

## Decision

Keep the modular monolith and existing authorized fixture ingestion pipeline.
Add an explicit results-v1 CSV profile; metadata-v1 retains its exact meaning and
publication keys. Results are provider-neutral, append-only observations with
original RAW context, reviewed identity, UTC availability and PostgreSQL INSERT
recording clocks. Only regulation-time HalfTime and FullTime are supported.
Unknown scores remain null. Extra-time and penalty totals cannot produce labels.

Validate scores, paired availability, status transitions and original identity
scope with versioned quality reasons. Source corrections form explicit ordered
chains; equally timed contradictory reports and independent source disagreements
fail closed rather than selecting a winner. Replays cannot reverse newer results.
Historical reads reapply availability, recording, identity, policy, retention and
RAW integrity gates. INSERT time is not COMMIT time (ADR 0017).

Keep existing finalized metadata dataset v1/v2 bytes unchanged. Introduce separate
result manifest/feature schema v3, freezing the existing metadata/coverage manifest,
eligible result evidence, quality/policy/identity decisions, feature inputs and
separate outcome label references. Observed statistics describe partial history;
metadata completeness does not prove result completeness. Ratios use exact integer
numerators and denominators. Labels may be known at a later evaluation cutoff but
never enter the pre-prediction feature input array.

Development-only read routes select the dedicated project-owned fixture source
and verify the exact registered fixture hashes before returning sanitized values.
No RAW paths, provider references, policy/quality audit records or mutations are
exposed. Missing database/configuration is explicit; no startup migrations/seeding.

## Consequences

One additive migration protects result observations and v3 artifacts from ordinary
UPDATE, DELETE and TRUNCATE. Non-owner runtime roles remain required; owners can
bypass triggers. Synthetic ingestion is an explicit Worker command. No live
transport, training, scoring, authentication or production UI is introduced.
