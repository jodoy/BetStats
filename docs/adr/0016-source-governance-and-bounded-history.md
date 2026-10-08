# ADR 0016: Source Governance and Bounded History

**Status:** Accepted

## Decision

Keep source policies and purpose permissions in Domain; evaluation and provider
contracts live in Application, PostgreSQL adapters in Infrastructure. Immutable
SourcePolicy versions contain effective intervals and evidence/terms references.
Append-only sequenced policy decisions supply Draft/Approved/Revoked state and
review/approval metadata. Approval is an internal review, never proof of legal
rights. Nine independent purposes use Allowed/Denied/Unknown; missing/unknown,
draft, revoked, expired and conflicting approvals fail closed. Usage context may
require additional commercial/display/training/redistribution permissions and
explicit attribution/retention restrictions. No provider permissions are seeded.

PostgreSQL records availability of policies, audit decisions and new identity
decisions using a trigger-controlled clock. Caller event dates cannot establish
historical availability. Existing identity history receives migration-time
availability because original trusted receipt times cannot be reconstructed.
Old rows/decision times remain intact but earlier queries conservatively exclude
them. Recorded time is insert time, not transaction commit time; historical reads
are not a long-lived transaction snapshot. Trusted database time and ordinary
application privileges are assumptions; privileged administrators can bypass guards.

Policies/permissions/audits are append-only, with RESTRICT FKs and unique source
versions/audit sequences. Approval locks the source and rejects overlapping active
approved intervals; revocation appends evidence rather than modifying approval.
Audit writes require READ COMMITTED or SERIALIZABLE; stale-snapshot isolation
levels are rejected rather than allowing a concurrent approval to remain hidden.
Application evaluation also fails closed for conflicting input. Evaluation results
identify policy/version, time, reasons and restrictions; evaluation does not claim
or confer contractual rights. No audit of individual provider executions exists yet.

Minimal provider contracts describe identity, sports, capabilities, configuration
validation and cancellable execution. A reusable executor authorizes the requested
purpose/context before acquiring a request budget and invoking an adapter. No HTTP
client or provider SDK is added. In-process budgets enforce sliding minute/day
counts, concurrency, timeout and Retry-After cooldown with TimeProvider. No automatic
retries are made, including authentication/permission failures. Budgets belong to
one provider/process and do not coordinate replicas or survive restarts.

Observation history gains keyset pages ordered by AvailableAtUtc/CreatedAtUtc/Id.
Cursors bind every query filter and cutoff; temporal filtering is always reapplied.
Page size has a configurable cap. The legacy list API works for small results and
throws on overflow instead of loading or silently truncating unbounded history.
New backdated observations inserted between pages can be missed behind a cursor;
pages are not a database snapshot. Immutable evidence prevents existing rows moving.

## Consequences

Additive migration preserves BS-002/003 migrations and ingestion/canonical rows.
Unit and real PostgreSQL tests cover fail-closed evaluation, concurrency/constraints,
audit, trusted availability, paging and existing regression suites. Before BS-005,
provider-specific terms and output rights must be independently verified and
documented. Credentials, copyrighted documents, provider downloads, ingestion,
statistics, predictions and UI remain out of scope.
