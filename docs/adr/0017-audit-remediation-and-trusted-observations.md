# ADR 0017: Trusted Observations and Audit Remediation

**Status:** Accepted

## Decision

PostgreSQL assigns Observation.RecordedAtUtc on INSERT, overwriting caller values.
AsOfUtc requires both AvailableAtUtc and RecordedAtUtc at or before the cutoff.
Event, publication, retrieval, creation and recording times remain distinct.
Existing observations receive migration-time recording availability, never guessed
historical availability. Their values and correction chains are preserved.

Historical RAW may be reprocessed. An observation linked to RAW must belong to the
same source and must not claim retrieval before RAW retrieval or availability or
creation before RAW creation. RAW dates remain capture claims, not independently
trusted database receipt evidence. They do not move a derived observation's
recording time backwards. A future reconstruction export may use separately
verified capture evidence under an explicit contract; ordinary history does not.

Recording is INSERT time, not COMMIT time. Delayed transactions can change a
reissued historical answer; this small change does not establish commit snapshots.
Keyset ordering remains availability/creation/UUID and reapplies both cutoffs.

Application defines an operational source-status port; Infrastructure reads current
source status without caching. Missing/disabled sources deny before budget or
adapter execution. Historical licensing evaluation remains independent.
The executor validates adapter result shape, error category/code and RetryAfter,
classifies unexpected exceptions without response text, and preserves caller
cancellation, timeout and concurrency leases. It never retries automatically.

RAW metadata rejects ordinary UPDATE, DELETE and TRUNCATE using PostgreSQL
triggers, including EF bulk operations. No application-controlled bypass exists.
Deployment must separate non-owner API/Worker roles from migration/maintenance
owners, revoke schema/function creation and trigger-altering privileges, and grant
only necessary operations. Administrators can still bypass these protections.

Retention uses a separately authorized privileged maintenance workflow, not an
application purge job. Record scope, rights basis, approval, operator and results
in an external protected audit ledger. Inventory dependent immutable provenance,
coordinate object storage deletion and metadata maintenance with an idempotent
manifest, and plan backups, recovery and deletion obligations before execution.
Restricted maintenance may temporarily alter protections under owner authority,
with exclusive maintenance access and verification/reinstatement afterward.
No unrestricted bypass or production role provisioning is implemented here.

## Migration and consequences

One additive migration preserves prior migrations and data. The volatile clock
default can rewrite observations and acquire strong table locks. Inspect SQL,
estimate table size/lock duration, back up and schedule maintenance before applying
to populated databases. Hosts never apply migrations automatically. Rollback
removes trusted availability and RAW guards and requires deliberate review.
Regression tests use disposable PostgreSQL 17 and synthetic data only.
