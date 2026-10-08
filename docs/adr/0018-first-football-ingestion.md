# ADR 0018: First Football Ingestion Slice

**Status:** Accepted

## Decision

Application owns bounded provider-neutral match records, RAW storage and ingestion
ports and orchestration. Infrastructure owns CSV parsing, local fixture transport,
filesystem storage and PostgreSQL normalization. Reuse AuthorizedProviderExecutor,
shared RequestBudget, SourcePolicy, IngestionRun, provider identities and observations.
Worker exposes an explicit synthetic demo command, never startup ingestion.

Football-Data.co.uk is a candidate format, not an authorized live source. Its
published restrictions prevent automatic approval for this product. This slice
implements no HTTP transport. Live configuration is rejected even if a flag or
internal policy claims permission. Synthetic fixtures have a separate source and
operator-reviewed fixture policy, never a grant to the real candidate.

CSV is strict UTF-8 with a bounded metadata-only profile. Season is explicit file
scope, not a fabricated CSV column. Match date is DateOnly, not guessed UTC kickoff.
Add only EventDate to the observation registry. Result indicator can establish
Completed; blank means unknown status, not an invented Scheduled status. No scores,
odds or statistics are normalized. Validation retains row numbers and fixed codes.

Only explicit reviewed identity decisions resolve competition/season/participants.
Teams use source/competition-scoped composite references, not global names.
Absent provider match IDs use labeled source/season/date/home/away composites.
Do not fuzzy-match or auto-create unknown teams. Once all context is reviewed,
create a new canonical event and an explicit audited identity decision; mismatched
existing mappings fail. Unresolved rows retain provider anchors and RAW traceability.

Capture exact bytes before parsing, SHA-256 and opaque random keys. Local storage
must be outside the repository, bounded, no overwrite, traversal/symlink rejection,
staging then atomic file rename within one volume, and verified reads. Filesystem
and PostgreSQL are not an atomic transaction: metadata may reference a staged
object, and reconciliation finalizes only after verifying metadata, hash and rights.
Orphans are inventoried for authorized maintenance, never automatically purged.

Source/purpose checks occur before retrieval, capture and publication. Database
publication transactions lock the source using the same lock as policy audits,
then re-evaluate rights. Separate RAW storage/retention/analytics purposes are
required. No caller-declared flag overrides denial. Audit events record denied,
running and terminal outcomes using fixed codes; interrupted runs remain visible.

Idempotency is source + competition + season + payload hash + parser version for
publication. Repeated attempts still have independent capture/run/audit evidence;
successful prior publication is reused, not silently duplicated. Resolved rows
also receive context/target-bound receipts so older partial payload replays cannot
reverse later corrections. Source locking
serializes publications. Partial/unresolved attempts may be reprocessed after
explicit mappings, with observation value comparison preventing duplicate facts.
Changed values append corrections, never update provenance. No distributed engine.

RAW and observations have independent database INSERT recording timestamps.
Legacy RAW receives migration-time availability; prior dates are preserved.
Normalization uses current creation/availability and cannot backdate derived facts.
Neither receipt timestamp is COMMIT time; historical reads are not commit snapshots.
Canonical rows are current projections, not historical evidence.

## Consequences

One additive migration preserves existing history, adds receipt time, EventDate,
structured execution audit and publication receipts. Tests use synthetic football,
isolated filesystems and disposable PostgreSQL 17. Recovery and privileged retention
remain explicit, bounded operator procedures. Real data ingestion, HTTP, scheduling,
production storage/roles, predictions and UI remain outside this milestone.
