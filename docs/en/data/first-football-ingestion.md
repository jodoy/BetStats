# First football ingestion (BS-005)

Decisions: [ADR 0018](../../adr/0018-first-football-ingestion.md).
Rights: [dated candidate assessment](football-data-assessment.md).
This is a synthetic vertical slice. Football-Data.co.uk is not an authorized
live provider. There is no HTTP client, network transport or automatic approval.
Setting Ingestion:LiveEnabled=true is rejected, even with an internally approved
policy. HTTP timeout/redirect/response-limit requirements belong to a separately
reviewed future transport; existing executor timeout/budget/cancellation applies
to the fixture adapter.

## Boundaries and pipeline

Application owns FootballIngestion and bounded provider-neutral records/ports.
Infrastructure owns CSV, fixture transport, filesystem and PostgreSQL mappings.
Domain contains no source CSV column names. API/Web are unchanged. Worker has an
explicit demo command; startup and ordinary hosting never ingest or migrate.

Each attempt records Running before authorization, using IngestionRun when the
source exists and an append-only audit event even for a missing requested UUID.
Executor checks configuration, source, rights, shared budget and fixture execution.
Capture rechecks enabled status, DataRetrieval, RawPayloadStorage and
HistoricalRetention; publication additionally checks InternalAnalytics. Unknown,
revoked, restricted-context or missing rights deny. No display/training/commercial
permission is implied. This slice cannot satisfy attribution/retention restrictions
and does not claim otherwise; those policies fail evaluation with undeclared context.

Capture/publication transactions use READ COMMITTED and lock the source, the same
lock used by approval/revocation writes. Rights are reevaluated within that lock.
This serializes database writes with policy audits, not filesystem operations or
already running adapters. Pending objects after denial are reconciliation evidence.

Original bytes are staged before metadata and parsing, hashed, saved as RAW,
finalized, verified on read, parsed, then normalized. Raw manifests are checked
against persisted source/hash/key/timestamps before publication. Retrieval success
does not mean import success. Invalid/unresolved rows produce Partial, and the
legacy IngestionRun status is Failed for Partial (terminal audit gives detail).
Structured terminal audits contain counts, fixed error category/code, row/code
issues, policy UUID and approval audit UUID. They never contain exception messages
or response bodies. A successful receipt is written in the publication transaction.

## CSV contract and identities

Metadata-v1 accepts strict UTF-8 (optional BOM), RFC-style quoted fields, at most
1 MiB, 5000 records, 16 columns and 4096 characters per field. Required headers:
Div, Date, HomeTeam, AwayTeam. Optional: FTR, Time, MatchId. Documented result
columns FTHG/FTAG/HTHG/HTAG/HTR are accepted but not normalized. Other headers fail
closed, including unsupported broad stats/odds schemas; this is not a universal
historical CSV importer. No score, player statistic or odds entity is added.

Season is explicit file scope, not invented from an absent column. Four-digit
dates use dd/MM/yyyy; two-digit years use an explicit century from season context.
Time is validated when present but no timezone/kickoff is inferred. EventDate
stores a calendar DateOnly. H/D/A only establish Completed; a missing result
leaves status unknown and does not manufacture Scheduled or a canonical event.

Official notes do not establish match IDs. When absent, labeled composite event
references hash source-scoped competition/season/date/home/away context. Team
references hash competition/name within a source; names are never global IDs.
Case/spelling remain explicit, with no fuzzy matching. Composite references are
not provider-issued IDs and cannot automatically link a rescheduled date or
distinguish two same-day encounters. Those need explicit review. Duplicate
references are reported and rejected, not silently ignored. MatchId is an optional
fixture/profile extension; the demo supplies fictional IDs explicitly, never
claims that the real provider issues them.

Competition, season and participant resolution use existing reviewed decisions.
Unresolved/ambiguous mappings retain anchors, RAW and observations with null frozen
targets. They do not create guessed teams. Exact reviewed football context may
create a new completed canonical event and an audited resolution. Existing event
mappings must match sport/competition/season/home/away or fail. Operators can
append reviewed decisions with existing Domain constructors; there is no mapping
UI or automatic approval endpoint. Reprocessing after review appends observations
with newly frozen targets; earlier records are never remapped.

## Idempotency and temporal provenance

Publication key = source + SHA-256 of competition/season/exact payload hash/parser
version. Complete successful publications have a unique receipt. Repeated attempts
still record new run/capture/audit evidence, but return Reused for that receipt.
Partial payloads may be reprocessed after review: existing observation values and
frozen targets are compared; unchanged facts are not duplicated. Changes append
corrections. Each resolved row also has a source/payload/context/target-bound receipt;
replaying an old partial file cannot reverse a newer correction. Unresolved rows
remain eligible for processing after explicit mapping review. Conflicting rows
with one supplied match ID reject both candidates, rather than selecting the first.
Source locking serializes concurrent publications. This is not a
global content deduplication scheme or a distributed rate limiter.

Source date/publication, fixture retrieval, RAW CreatedAtUtc/RecordedAtUtc and
Observation CreatedAtUtc/RecordedAtUtc are distinct. New RAW receipt and observation
recording are overwritten by PostgreSQL on INSERT. Legacy RAW retains original
dates and gets conservative migration-time receipt; ByteLength is unknown/null.
Normalization uses current database availability/creation and both historical
cutoffs remain enforced. DateOnly does not fabricate a source UTC event instant.
Recording timestamps are INSERT, not COMMIT, and pages are not transaction snapshots.

## Local durable storage and recovery

Ingestion:RawStoragePath (environment Ingestion__RawStoragePath) must be absolute
and outside any repository. Default: LocalApplicationData/BetStats/raw. Filesystem
storage is a replaceable local implementation, not production object storage.
Use a private directory owned by the execution account; administrators and other
users with filesystem write access remain outside the trust boundary. Directory
and file links are rejected, but checks do not defeat an adversarial simultaneous
filesystem replacement by a privileged actor.

Keys are random 32-character UUID hex. CreateNew prevents overwrite. Stage writes
exact bytes to key.pending and flushes to disk; same-volume rename finalizes to
key.raw. Reads check bounded length and SHA-256. No user path/URL is stored in keys.
There is **no atomic transaction across PostgreSQL and filesystem**, nor a claim
of crash-proof production replication/directory durability.

| Interruption | Explicit recovery |
| --- | --- |
| Partial staging/no metadata | Inventory key.pending; orphan needs authorized maintenance. Never auto-delete. |
| Metadata committed/finalization failed | IngestionRecovery.ReconcileStagedAsync checks rights, manifest and hash, then finalizes. |
| Publication committed/audit incomplete | Inspect receipt and run; MarkInterruptedAsync can reconstruct completion from receipt after operator confirms owner is stopped. |
| Running/no receipt | Operator confirms owner stopped, appends Interrupted; replay as a new attempt. |
| Missing/corrupt object | Fixed failure code; investigate manifest/storage backup under authorized recovery. |

Inventory batches are capped at 1000. Legacy/null length or ambiguous metadata
requires manual manifest verification. Storage reconciliation returns structured
outcomes and must be retained in the operator's protected maintenance audit.
No scheduler/purge or unrestricted deletion bypass is supplied. DB unavailability
can prevent terminal auditing; the durable Running record and objects remain for
later reconciliation. Completed audit events are immutable; later retries are
new attempts, not edits. Full cross-system operational auditing remains future work.

## Run the synthetic demonstration

Use a dedicated local development database with Docker Linux containers. Follow
[connection setup](persistence-foundation.md); never use production credentials.
Apply migrations explicitly:

```sh
dotnet tool restore
dotnet ef database update --project src/BetStats.Infrastructure
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
```

Set ConnectionStrings__BetStats securely through the documented environment
configuration, and optionally Ingestion__RawStoragePath to a private external
directory. The explicit --approve-synthetic option reviews only project-authored
fictional fixtures and exact mappings. It never approves the candidate provider.
Existing source/policy state is not silently repaired or reapproved. No setup
is called on startup or by the normal ingestion use case.

The fictional Lantern League has one season and four mapped teams. Fixture rows
include invalid date, duplicate and Unmapped Ravens. Four runs demonstrate initial
Partial, repeated Partial without duplicate facts, a successful date correction,
then Reused. Dates/events are fictional; no real team names or provider data appear.

Inspect only non-sensitive metadata with psql using existing local credentials:

```sql
SELECT "Id", "Status", "ErrorCode" FROM ingestion."IngestionRuns";
SELECT "AttemptId", "Outcome", "ParsedRecords", "AcceptedRecords", "RejectedRecords",
       "UnresolvedIdentities", "ErrorCategory", "PolicyId" FROM ingestion."IngestionAuditEvents";
SELECT "Id", "StorageKey", "ByteLength", "ContentHashSha256", "RecordedAtUtc" FROM ingestion."RawPayloads";
SELECT "Id", "Type", "DateValue", "Version", "CorrectsObservationId", "RecordedAtUtc" FROM provenance."Observations";
```

Resolve key.raw under your configured external root and compare Get-FileHash -Algorithm
SHA256 or sha256sum with metadata; do not paste actual provider bytes/logs into Git.
Tests use fresh disposable PostgreSQL and isolated temporary folders and clean up
their own fixtures. For another clean local demo, prefer a new dedicated database
and new storage root. Deliberate destruction of an exclusively local Compose
volume follows the existing documented reset procedure; it is not licensed-data
retention. Never reset shared/production storage or disable guards to tidy a demo.

## Migration, tests and next milestone

20261008083514_FirstFootballIngestion is additive: RAW receipt/length, DateValue
and extended value constraint, two append-only audit/receipt tables and triggers.
Existing rows/migrations/RAW mutation guards are preserved. The volatile receipt
default may rewrite RAW and acquire strong locks; plan backup and maintenance.
Rollback destroys new audit/receipt information and cannot preserve EventDate
semantics; review before any deliberate rollback. Hosts never migrate automatically.

Tests cover parser/storage contracts, SHA-256, source/policy denial, repeated and
concurrent execution, review/collisions, boundary revocation, storage/metadata
failure, recovery, cancellation, temporal corrections and fresh/BS-004.1 upgrade.
Existing provider timeout/lease tests and all earlier suites remain in CI/CodeQL.

Recommended BS-006: operator mapping/reconciliation tooling and protected execution
audit, or one independently permitted source with a reviewed safe transport. Do
not enable this candidate for commercial/training use on the current evidence.
