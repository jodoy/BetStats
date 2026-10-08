# ADR 0020: Frozen Datasets and Leakage-Safe Metadata Features

**Status:** Accepted

## Decision

Application owns bounded, explicit dataset definitions, evidence contracts, canonical
serialization and pure C# feature calculations. Infrastructure assembles PostgreSQL
evidence and stores the exact UTF-8 artifact and individual vectors in PostgreSQL.
This avoids filesystem/database publication atomicity problems. No provider transport,
prediction model, public API or scheduling is added.

Definitions require scope, season bounds, historical cutoff, mode, reconstruction
cutoff when applicable, purpose, usage context, quality/schema versions, explicit
target observation IDs and per-target prediction cutoffs. Date-only targets require
an explicit UTC-calendar interpretation and prediction strictly before that calendar
day. This is an operator assertion about the fictional fixture, never inferred kickoff.
Unsupported time bases fail closed. Scope and participant relationships come from
the captured RAW row and time-bounded identity decisions, not mutable canonical
event projections. RAW bytes are verified before parsing. No scores are consumed.

Historical mode bounds observations/RAW by both availability and trusted INSERT
recording time, and interpretation by each target prediction cutoff. Retrospective
mode retains those observation/RAW cutoffs but bounds decisions/quality/policies
by the explicit reconstruction time. Both record their exact interpretation evidence.
Dataset AsOf bounds the target metadata; it is not the row's feature cutoff.
Unknown/cancelled/future/target/same-day facts cannot become completed history.

Assembly uses REPEATABLE READ without source row locks. All queries, including the
existing BS-006 gate, share that transaction. INSERT is not COMMIT: the artifact
freezes only committed rows visible in that PostgreSQL snapshot. Reissuing a query
after a late commit may change output, which must receive a different content key.
After assembly closes, a short READ COMMITTED finalization transaction acquires
sorted source locks shared with ingestion, review and policy audits, then revalidates
enablement, all needed current purposes and retention. This prevents revocation
from committing between authorization and publication. A revocation after publication
does not mutate history and denies subsequent use. No external I/O occurs under locks.

Only observed metadata is currently supportable: observed completed-match counts
and days since the last *observed* completed match. They are explicitly partial-history
features, not total activity or actual rest. Absent history and missing status return
unavailable, not zero. Complete season/count/rest claims require future independently
verified coverage evidence. Season bounds are explicit request constraints, not
historical facts inferred from mutable Season rows.

Serializer v1 recursively orders object keys using ordinal comparison, preserves
explicitly sorted arrays, normalizes strings to Unicode NFC, writes explicit nulls,
invariant integers and UTC microsecond timestamps, and emits compact UTF-8 JSON.
Floating-point values are absent. SHA-256 covers exact artifact bytes; feature hashes
cover each frozen vector's inputs and ordered values. Changing serialization or
feature meaning requires a new supported schema version. UUIDs identify provenance;
build UUIDs/times are excluded from content hashes. Unique content hashes serialize
identical finalizations using a transaction advisory lock. Every request still has
its own durable Requested/Running/terminal attempt ledger. Changed evidence creates
a new immutable snapshot even for the same definition.

Snapshot/vector tables reject UPDATE, DELETE and TRUNCATE, including bulk EF SQL;
trusted recording triggers replace caller timestamps. No unrestricted bypass exists.
Attempts are append-only and terminal writes serialize on their attempt lock.
Cancelled/error builds publish nothing; transaction rollback protects artifact writes.
Process death may leave Running. A confirmed-stopped owner can be explicitly marked
Failed by an operator; this is not lease detection. Database unavailability may also
prevent recording the failure; never claim success without committed publication.

Verification independently reports byte/hash integrity, evidence completeness,
current permission, and recalculation from frozen feature inputs. Integrity does not
confer permission. Read commands validate current rights before returning content;
verification can report metadata/integrity after denial without returning evidence.
Comparison is read-only, bounded and ordered with an explicit offset.

## Limitations and next milestone

Synthetic UTC-calendar fixtures only; existing football metadata parser/rules v1.
No historical completeness certification, authentic operator identity, production
storage/roles, external datasets, ML or trained/validated model. PostgreSQL owners
can bypass triggers; deploy ordinary non-owner runtime roles (ADR 0017).
BS-008 should add independently governed coverage evidence and precise event-time
provenance before claiming complete activity/rest features or backtesting.
