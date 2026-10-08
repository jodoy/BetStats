# ADR 0024: Result coverage, durable operations and event-end provenance

**Status:** Accepted

## Decision

BS-010 starts at main `5678dc2`, after the confirmed merge of BS-009 PR #13.
Keep the modular monolith, RAW-first capture, source locks, historical identity and
quality gates. Add independent result inventory assertions scoped to regulation-time
finished results, a bounded UTC-calendar interval and optional participant. No
metadata coverage assertion establishes result completeness. Only a reviewed,
project-owned fictional closed-inventory RAW contract can support Complete/Empty.
Unknown, Partial, Conflict and Expired remain distinct; missing, conflicting,
unavailable or unsupported evidence fails closed. Reviews and corrections are append-only.

Add source-bound event-end claims with original result RAW context, reviewed identity,
explicit precision, publication/retrieval/availability and trusted recording clocks.
Reuse the event-time resolver. Never derive end from kickoff or elapsed duration.
Evaluation contract v3 requires precise end evidence known by evaluation, separated
from prediction, and consistent with the justified event boundary and outcome.
Existing evaluation contract v2 keeps its exact semantics.

Add an append-only dataset-v3 operation ledger. Caller operation UUID and canonical
request fingerprint establish idempotency. Short advisory-locked transactions issue
DB-clock leases and fencing tokens. Explicit recovery may replace only expired
owners; failed/cancelled attempts may retry. Live leases cannot be stolen. Artifact
publication and successful terminal ledger append commit together, under the
operation lock and source authorization locks. Old owners cannot publish after
expiry/recovery. Content hashes still deduplicate independent operations. No
automatic scheduling, migration, import or recovery occurs at startup.

New v3 builds optionally freeze governance extension v1 (independent result coverage
and event-end evidence); legacy v3 without it remains verifiable. Optional JSON
members are omitted when absent. Finalized v1/v2/v3 bytes are never rewritten.
Operator commands are Development-only, require explicit actor/reason, and require
approval for mutation. An actor string is an audit claim, not authentication.

Clock-controlled fictional fixtures replace current-date assumptions for new
scenarios. Legacy BS-009 demo payloads and display hash allowlist remain compatible.
No real provider transport, model training, scoring, production API or UI is added.

## Consequences

One additive migration adds result inventory/reviews, end claims and operation events
with trusted insert clocks, relationships and ordinary UPDATE/DELETE/TRUNCATE guards.
Runtime roles must not own tables; database administrators can bypass guards.
Lease expiry may cancel slow work; operators explicitly recover it with the same
fingerprint. Database failure can leave Running; committed success is authoritative.
Closed inventory certifies only the declared fictional result universe, never a real
provider or real-world season. DateOnly/unknown end evidence cannot authorize evaluation.
