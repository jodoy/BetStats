# BS-010 result coverage, v3 operations and event end

## Starting point and boundaries

PR #13 was initially open. Before implementation, GitHub confirmed its merge;
the BS-010 branch starts at main `5678dc2` (BS-009). See [ADR 0024](../../adr/0024-result-coverage-operations-and-event-end.md).
There are no real provider requests, training, scoring, new production API routes or UI.
Existing v1/v2 artifact formats and calculations are unchanged. Legacy v3 artifacts
omit the optional new governance extension and retain their bytes/hashes.

## Independent result coverage

Scope identifies source, competition, season, original scope references, optional
participant and a half-open UTC-calendar interval. The subject is **eligible finished
regulation-time result observations**, independently of metadata completeness.
An observed count never establishes a complete universe.

| Status | Meaning |
|---|---|
| Unknown | No usable independently approved inventory at the knowledge cutoff |
| Partial | Reviewed limited inventory, or gaps between strong intervals |
| Complete | Reviewed exhaustive fictional closed inventory covers the exact interval |
| Empty | Reviewed exhaustive fictional closed inventory explicitly contains no finished results |
| Conflict | Contradictory results/strong assertions, or a reviewed inventory no longer matches known results |
| Expired | Only expired usable inventory assertions remain |

Only `project-owned-result-inventory-v1` RAW with a source policy whose terms are
`synthetic:owned-fixture` can establish strong claims. The envelope contains version,
scope, claim, expected provider event references and exact result observation UUIDs.
Complete requires nonempty evidence; Empty requires explicit empty arrays. Partial
may declare a verified subset. Inventory checks reapply result RAW, historical
identity, quality and current licensing/retention gates. Metadata-only finished
events, including later status changes on unchanged dates, invalidate strong claims.
Unknown metadata status cannot establish absence. Future calendar intervals cannot
be certified complete/empty. This certifies a declared **fictional** universe only.

Evidence and RAW must be available and recorded by AsOf. HistoricalAsKnown reviews
and identities must also be known by AsOf. RetrospectiveReconstruction has an explicit
later interpretation cutoff and never backdates evidence. Current rights and current
expiry remain additional gates. Later reviews cannot upgrade an earlier historical
answer. A result correction invalidates the old inventory for later queries until an
explicit successor inventory is recorded and reviewed; earlier cutoffs retain the
earlier evidence. Reviews have expected sequence checks under the existing source lock.
Missing/corrupt inventory RAW is unavailable, never an Empty result.

## Explicit event-end provenance and evaluation

`EventEndSourceClaim` v1 RAW binds original result RAW UUID, result observation UUID,
provider event reference, original competition/season context, explicit EventTimeValue
and nullable PublishedUtc. Submission publication must match the RAW envelope; absence
stays null. Claims bind the reviewed event identity and capture policy and distinct
retrieval/availability/recording clocks. DateOnly/Unknown remain uncertain; the existing
resolver handles minute/second, offsets, DST gaps/ambiguity and inconsistent source UTC.
No kickoff-plus-duration calculation exists. Ends after their RAW retrieval, wrong
original context or noneligible/nonfinished results are rejected. Independent
contradictory precise claims fail closed. A correction must name its predecessor,
remain in the same source identity stream and have newer capture evidence.

Evaluation definition v3 requires independent Complete result coverage and eligible,
source-bound minute/second end evidence tied to the exact outcome and event. The end
must follow prediction and the justified event boundary and precede label availability;
both end clocks must be known by evaluation. Correction versions are explicit.
Definition v1/v2 eligibility retains its existing semantics (response contract v2);
new definition v3 reports contract v3. These are eligibility contracts, not an executor
or model-performance assertion. End provenance belongs to later label governance and
cannot enter pre-prediction feature hashes.

## Durable dataset-v3 operations

Each build requires caller OperationId, Dataset request, actor, reason and approval.
Fingerprint covers metadata definition, label cutoff and governance-extension version;
actor/reason are audit claims and are excluded from content identity. The append-only
ledger moves Requested -> Running -> Succeeded/Failed/Cancelled. Short transactions
serialize on the operation UUID. Running receives a fresh owner token and a ten-minute
DB-clock lease. Live owners are never stolen. Callers seeing Running must inspect and
explicitly recover after expiry; there is no background retry or startup recovery.

Recovery requires the exact expected fingerprint, actor/reason/approval and an expired
Running or Failed/Cancelled state. It issues a new owner token without changing the
stored request. Finalization checks token, sequence and unexpired lease under the
operation lock. Artifact insertion/reuse and Succeeded append commit atomically, so
an old owner cannot publish after recovery. Separate operations with identical frozen
content reuse the same artifact hash. A succeeded replay rechecks current permissions.
Fingerprint mismatch fails; successful history is never reopened or rewritten.

Assembly failures publish no result artifact, though an independently valid metadata
snapshot/attempt may already exist. Database loss can leave Running. Slow work may
outlive its lease and require explicit recovery. Leases have no automatic renewal.
The ledger, including the stored request, is bounded to 1 MiB per request. PostgreSQL
INSERT time is not COMMIT time; consistent assembly freezes committed snapshot visibility.

New operation builds freeze result-governance extension v1: independent feature/label
coverage and target end evidence. The result feature schema remains v3 and explicitly
describes partial observed statistics. Standard verification separates immutable byte
integrity, frozen feature reproduction and current authorization. Deep verification
additionally checks current permission/retention before RAW reads and reports nullable
RAW availability/hash flags. RAW loss does not rewrite or invalidate immutable bytes.

## Worker runbook

Configure local `ConnectionStrings__BetStats` and absolute `Ingestion__RawStoragePath`
outside the repository. Do not commit credentials or operator payload files.
Apply migrations **explicitly**:

```powershell
dotnet ef database update --project src/BetStats.Infrastructure
$env:DOTNET_ENVIRONMENT = 'Development'
```

All new actions use `Results:Action`, require `Results:OperatorId` and `Results:Reason`,
and mutations require `Results:Approve=true`. They cannot be combined with Dataset,
Coverage, Quality or synthetic-demo host actions. Exit 0 means success; Running,
Failed, Cancelled, rejected approval or negative verification returns nonzero.
Read-only coverage/ends/operation/inspect commands return the bounded report even when
coverage is Unknown. Actor strings are manually supplied audit claims, not authentication.

| Action | Additional arguments |
|---|---|
| `demo-clock` | `SourceCode`, `Approve=true` |
| `capture-evidence` | `SourceId`, `ScopeJson`, local `PayloadPath`, `Approve=true` |
| `record-inventory` | `SubmissionJson` (ResultInventorySubmission), `Approve=true` |
| `review-inventory` | `SubmissionJson` (ResultReviewRequest with expected sequence), `Approve=true` |
| `coverage` | `QueryJson` (ResultCoverageQuery) |
| `record-end` | `SubmissionJson` (EventEndSubmission), `Approve=true` |
| `ends` | `QueryJson` (FootballResultQuery) |
| `build-v3` | `OperationId`, `RequestJson` (FootballResultDatasetRequest), `Approve=true` |
| `operation` | `OperationId` |
| `recover-v3` | `OperationId`, `ExpectedFingerprint`, `Approve=true` |
| `inspect-v3` | `SnapshotId` |
| `verify-v3` / `verify-v3-deep` | `SnapshotId` |

Use the existing strict canonical JSON contracts (UTC microseconds and enum names).
Capture inventory/end JSON before submitting its returned RAW UUID. `capture-evidence`
reuses the authorized ingestion capture/receipt audit and original scope binding;
it parses no football entities and has no network adapter. Result record/review/operation
rows durably retain actor and reason. Capture returns RAW identity and actor/reason;
the existing ingestion audit format does not add an authenticated operator identity.
Review's `Approve` is the requested decision; `Results:Approve` authorizes execution.
Never make up RAW IDs, observed references, inventory membership or event-end timestamps.

```powershell
dotnet run --project src/BetStats.Worker --configuration Release -- --Results:Action=demo-clock --Results:SourceCode=synthetic-clock-demo --Results:OperatorId=operator:local --Results:Reason="Explicit owned fixture demo" --Results:Approve=true
```

The new scenario derives business dates from the injected TimeProvider: history -3/-2
days, postponed -1, target +2; a subsequent explicit correction changes 2:1 to 2:2.
No year is assumed. The database still assigns recording clocks. Repeating demo payloads
for the same clock anchor demonstrates reuse. Running on a different day changes fixture
evidence; use a fresh source code for that scenario rather than overwriting an old scope.
The old `Results:Action=demo --approve-synthetic` retains byte-identical BS-009 2026
payloads and its separate public hash allowlist; the new clock demo grants no display rights.

For v3, use a valid metadata-v2 definition with actual target date-observation IDs and
explicit prediction, dataset and label cutoffs. Compute the recovery fingerprint with
`ResultDatasetOperations.Fingerprint(request)` or read it from the `operation` command.
`operation` exposes status/sequence/snapshot/hash/failure/fingerprint only; no payload contents.
No administrative HTTP endpoint exists. No host automatically migrates, seeds or imports.

## Migration and operational limits

`20261008221019_ResultCoverageOperationsEventEnd` adds ResultInventory,
ResultInventoryReviews, EventEnds and ResultOperations. Trusted clock/source/sequence/
lease/correction triggers and RESTRICT FKs protect history. Ordinary UPDATE, DELETE and
TRUNCATE fail. Runtime users must be non-owner roles; database administrators can bypass
guards. No production role provisioning, authentic operator identity or lease heartbeat
service is implemented. Use backups and reviewed SQL for real deployments. Down removes
the new histories deliberately; no finalized artifact format is rewritten.
