# Audit remediation (BS-004.1)

[ADR 0017](../../adr/0017-audit-remediation-and-trusted-observations.md) updates
the BS-002–004 guarantees. No external provider, payload download or purge job is added.

## Trusted observation availability

Observation.RecordedAtUtc is PostgreSQL clock_timestamp() assigned on INSERT.
EF ignores supplied values; a database trigger overwrites explicit SQL values.
History and every keyset page require both RecordedAtUtc <= AsOfUtc and
AvailableAtUtc <= AsOfUtc. Ordering remains AvailableAtUtc/CreatedAtUtc/UUID;
correction chains, frozen canonical references and source filters are unchanged.

SourceEventTimeUtc is the source event time; SourcePublishedAtUtc is publication;
RetrievedAtUtc is claimed acquisition; CreatedAtUtc is observation creation;
AvailableAtUtc is claimed consumer availability. None substitutes for database
recording evidence. Effective visibility is no earlier than both availability
and recording. A newly persisted observation cannot become visible at a cutoff
before its insertion even when all caller timestamps are backdated.

Delayed normalization of historical RAW remains permitted. The source must match
the RAW source; observation retrieval must not precede RAW retrieval, and observation
availability/creation must not precede RAW creation. PostgreSQL checks these links
on INSERT, including direct SQL. RAW timestamps are preserved capture claims;
they do not themselves prove trusted historical receipt. Independently verified
earlier capture evidence may support a future explicitly defined reconstruction
export, but does not override this query contract. Corrections are new recordings
and have their own availability barrier.

RecordedAtUtc is INSERT time, not COMMIT time. A delayed transaction can become
visible after a cutoff that follows its INSERT. Reissuing a historical query is
therefore not a commit-time snapshot guarantee. Database clock and ordinary
privileges are trusted. Pages also do not create a transaction snapshot; a fixed
dataset/export needs a separately controlled snapshot. No stronger commit-visible
model is implemented in this small corrective milestone.

## Source gate and provider result boundary

ISourceOperationalStatus lives in Application. Infrastructure reads the current
DataSource without caching or tracked-entity state. Missing/disabled sources return
PermissionDenied with source_missing/source_disabled before budget acquisition or
adapter execution, even with an approved policy. Status is read per request and
again after licensing evaluation at the execution boundary. Historical licensing
evaluation does not depend on current operational status. Disabling a source does
not cancel an already running adapter; coordination remains future integration work.

Null results, inconsistent success/error, undefined categories, blank/null codes
and invalid RetryAfter return InvalidResponse. RetryAfter must be nonnegative,
representable at the current clock and associated with RateLimitExceeded.
Unexpected execution exceptions return a fixed provider_execution_failed code
under TemporaryUnavailability without exception text or response bodies. Caller
cancellation propagates; timeout and leases retain BS-004 behavior. No automatic
retries or external calls are introduced.

## RAW integrity and authorized retention

ingestion.RawPayloads rejects UPDATE, DELETE and TRUNCATE using a statement trigger
and the existing provenance rejection function. EF bulk writes and direct SQL are
covered. INSERT and RESTRICT foreign keys remain supported. This protects metadata;
it does not make external object storage immutable or capture timestamps trusted.

Future deployment uses distinct roles:

| Role | Responsibility |
| --- | --- |
| API/Worker execution | Non-owner, non-superuser, no privileged-role membership; only necessary SELECT/INSERT and explicitly scoped mutable-table UPDATE. No RAW UPDATE/DELETE/TRUNCATE, schema CREATE or function/trigger DDL. |
| Migration owner | Non-runtime identity owning schema/tables/functions; controlled DDL credentials and deployment access. |
| Maintenance operator | Separately authorized, time-limited access to the maintenance owner workflow; no grant to API/Worker. |

Provisioning must revoke public CREATE privileges on application schemas and audit
default privileges, table ownership, role membership and function ownership. A
deployment-specific grant script should explicitly enumerate tables and operations;
blanket ALL grants to runtime roles are inappropriate. No production roles are
provisioned by this migration. Database owners/superusers can disable triggers.

Retention is a privileged maintenance procedure:

1. Obtain explicit authorization identifying legal/licensing basis, scope and deadline;
   inventory RAW objects and dependent immutable observations/decisions.
2. Create a protected external audit record and an idempotent maintenance manifest
   with approvals, operator, identifiers, planned actions and recovery boundaries.
3. Plan database/object-store backups and restoration validation, including deletion
   obligations for backups and preventing restored data from reappearing improperly.
4. Quiesce affected writers/readers. Coordinate object deletion and dependent
   metadata handling with manifest states; cross-system atomic deletion is not assumed.
5. Use restricted owner maintenance access for explicitly reviewed DDL/data changes
   if necessary; never an application boolean/session-setting bypass. Reinstate and
   verify protections, foreign keys, row counts and object-state reconciliation.
6. Record completion/failure and authorized recovery actions in the protected ledger.

No automated purge, reusable unrestricted deletion procedure or retention bypass
is implemented. RESTRICT dependencies must be addressed deliberately; rollback of
migrations is not a retention tool.

## Migration and verification

20261008014627_AuditRemediation is one additive migration. It adds observation
RecordedAtUtc and trusted INSERT/RAW-link validation, and guards RAW mutations.
Legacy observations receive migration-time database availability. Original rows,
caller dates, RAW metadata, correction links and identity references are preserved;
queries before migration conservatively exclude legacy observations. Legacy RAW
temporal links are not retroactively rejected or silently repaired. New inserts
must meet the new checks.

The volatile default may rewrite the observation table and holds strong DDL locks.
Inspect the generated SQL, plan maintenance/lock duration and backup on populated
databases. Hosts never migrate automatically. Down removes the new barriers and
trusted column; deliberate rollback requires reviewing the lost evidence semantics.

Permanent tests cover fresh/BS-004 upgrades, data preservation, UTC microseconds,
late inserts, delayed RAW normalization, corrections, paging, source gates,
malformed responses, cancellation/timeout and bulk/direct SQL RAW mutation.
Testcontainers uses disposable PostgreSQL 17; missing Docker fails rather than skips.
Existing CI and CodeQL workflows are preserved.

BS-005 may begin with a limited, independently permitted ingestion slice after
these gates pass. Provider rights, durable execution audit, payload storage,
retention enforcement and any fixed-dataset requirements still need explicit design.
