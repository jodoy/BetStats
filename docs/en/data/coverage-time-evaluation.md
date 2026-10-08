# Historical coverage, event time and evaluation contracts (BS-008)

See [ADR 0021](../../adr/0021-coverage-time-evaluation.md). These contracts describe
evidence; they do not establish completeness of real-world sport or execute models.

Coverage has exact source/sport/competition/season/optional participant/event type/
observation type dimensions. Intervals are half-open `[start,end)`, bounded to 731
days. Calendar intervals carry an explicit basis and are never converted to exact
UTC intervals. Adjacent intervals do not overlap. Reports contain up to 200 claims,
stable IDs, gaps, partial/conflicting intervals, expired claims, missing observation
types, permission status and explicit T/R. Oversized requests fail rather than truncate.
There is no percentage with an unknown denominator.

`Unknown` has no defensible approved coverage; `Partial` has limited reviewed evidence;
`VerifiedComplete` has affirmative exhaustive evidence; `VerifiedEmpty` affirmatively
proves an empty scope; `Conflicting` fails closed; `Expired` cannot be reused. Counts,
continuous dates and successful ingestion establish neither complete nor empty scopes.
Claims and reviews are separate immutable histories. An unreviewed strong claim is
Unknown. A rejected later review does not erase earlier historical approval.

The only strong basis implemented is `project-owned-fixture-inventory-v1`: a RAW JSON
`CoverageInventory` contains Contract, exact Scope, Claim and sorted ObservationIds.
It is restricted to SourcePolicy terms `synthetic:owned-fixture`. A reviewer must
independently identify the closed fictional fixture inventory, verify its exact scope,
RAW hash and exhaustive observation list. The service also verifies known scoped
observations, historical identity and quality. Quality-denied or unresolved potential
events cannot disappear into a verified-empty claim. This is a guarantee about the
owned fictional fixture, not a guarantee about a real provider. Manual statements
cannot receive strong approval. Real provider completeness contracts are not enabled.

Every assertion records its RAW/hash, policy, version, publication (if known), retrieval,
availability, trusted DB INSERT receipt and expiry. SQL triggers overwrite caller
recording times and reject UPDATE, DELETE and TRUNCATE, including EF bulk operations.
EF additionally rejects tracked mutation. Review streams require consecutive sequence
and source locks; corrections have explicit same-stream predecessors. Owners can
bypass triggers: use restricted runtime roles as described in ADR 0017.

Historical queries require evidence, RAW and availability by T; review, identity and
quality interpretation use T, or explicit R in `RetrospectiveReconstruction`. A later
approval cannot improve HistoricalAsKnown. Current source permissions, retention and
expiry remain additional use gates. INSERT receipt is not COMMIT time; immutable
dataset assembly retains BS-007 transaction visibility guarantees. Disabled/revoked
sources deny reports/inspection and artifact use without erasing integrity evidence.

Event claims separately store local date/time, timezone, explicit offset, source UTC,
precision and provenance clocks. DateOnly never creates kickoff. Minute/Second require
matching local precision and .NET TimeZoneInfo context. Missing/unsupported timezone,
DST gaps, ambiguous DST without compatible explicit offset, offset conflict, source UTC
disagreement and overflow yield uncertainty. Explicit corrections preserve earlier
claims; independent reschedules remain conflicts. Timezone rules come from the host;
manifests freeze the resolution actually used rather than recomputing a past instant
under a different installed timezone database.

The four BS-007 metadata features declare observation types, participant, window,
Completed status and quality v1 requirements. They preserve their observed-history
names and partial qualification. Unknown/Partial can support only explicitly partial
features; conflicting/expired/unauthorized evidence blocks values. No missing value is
converted to zero. Strong coverage does not rename observed counts into true activity.

Definition/feature/manifest v2 freezes coverage claims and versions, reviews, identity,
quality, policy, RAW, gates, precision, uncertainties and schema v1 rules, including
time claims for the target and relevant historical events (at most 200 per row). v1 extensions
are omitted from canonical JSON: original bytes/hashes/calculators remain supported.
New evidence is visible only at a new cutoff (or explicit R) and creates a new immutable
artifact. Rebuilding an unchanged definition yields the same content hash. Dataset
verification checks frozen records and recomputes features/gates separately from current
authorization. Coverage currently supports the football date/status vertical slice;
UTC interval algebra exists, but a provider UTC completeness contract is not implemented.

Future evaluation definitions cover winner, total goals, BTTS and first-half goal
occurrence. They declare cutoff/horizon, sport/mode, required evidence, outcome type,
coverage/quality and metrics. Feature evidence must be available by prediction;
labels arrive afterward and must have trusted receipt by evaluation. Corrections need
new explicit versions. Permissions and complete coverage are separate gates. These
are contracts only: callers must supply actual permitted outcome evidence and a real
feature manifest before any future executor could make an operational eligibility claim.
No scores, outcomes, probabilities, metrics or model performance are computed here.

Log loss/Brier/accuracy use categorical formats, calibration uses binary predictions
and ten fixed bins, and MAE uses expected-count/integer-count formats. Each v1 contract
requires at least 100 samples, nonnegative finite weights with positive total, explicit
global aggregation and exclusion/reporting of missing labels. Binary calibration cannot
be applied to the three-class winner target. MAE is limited to total goals.

## Explicit local Worker operations

Use a dedicated disposable development database, apply migrations explicitly, and keep
RAW storage outside Git. ConnectionStrings__BetStats and Ingestion__RawStoragePath are
environment overrides; do not commit credential values. There is no startup migration,
provider request, scheduler or public administration API. Never point these commands
at a production database. `OperatorId` is manually supplied, not authenticated.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Worker --configuration Release --no-build -- --Coverage:Action=evaluation-contracts --Coverage:OperatorId=operator:local --Coverage:Reason="Inspect future contracts"
```

`Coverage:Action=record` accepts `Coverage:SubmissionJson` (CoverageSubmission), and
`record-time` accepts EventTimeSubmission. Their RAW must already be captured/finalized
in the private store with matching metadata. All commands require OperatorId/Reason;
the caller identity replaces JSON identity fields. `inspect` uses EvidenceId; `report`
uses QueryJson (CoverageQuery); `time` uses IdentityId and QueryJson. `feature-gate`
accepts 1–10 QueriesJson and RequirementJson (FeatureCoverageRequirement). Use canonical
JSON UTC timestamps with six fractional digits, explicit GUID scope and enum strings.

Approval is explicit, never automatic:

```powershell
# Replace the placeholder with an actual fictional evidence ID.
dotnet run --project src/BetStats.Worker --configuration Release --no-build -- --Coverage:Action=review --Coverage:EvidenceId=<fictional-evidence-guid> --Coverage:ExpectedSequence=0 --Coverage:Decision=Approved --Coverage:BasisReference=project-owned-fixture-inventory-v1 --Coverage:OperatorId=operator:local --Coverage:Reason="Reviewed exhaustive owned fixture"
```

Successful writes append durable claim/time/review history. Read operations print actor,
reason and result; they do not change evidence history. `Dataset:Action=build` accepts
DefinitionJson and OperatorId/Reason; set Version=2 and FeatureSchemaVersion=2. Existing
BS-007 build-synthetic behavior remains v1. There is no approval implicit in a build.

Read-only SQL on the disposable fixture database:

```sql
SELECT "Id", "SourceId", "Scope", "Claim", "Version", "RawHash", "RecordedAtUtc", "ValidUntilUtc"
FROM coverage."Evidence" ORDER BY "RecordedAtUtc", "Id" LIMIT 100;
SELECT "EvidenceId", "Sequence", "Status", "BasisReference", "RecordedAtUtc"
FROM coverage."Reviews" ORDER BY "EvidenceId", "Sequence" LIMIT 100;
SELECT "Id", "ProviderIdentityId", "Value", "CorrectsId", "Version", "RecordedAtUtc"
FROM coverage."EventTimes" ORDER BY "RecordedAtUtc", "Id" LIMIT 100;
```

The additive migration `20261008134149_CoverageEventTimeEvaluation` creates three
tables/six history triggers and permits snapshot feature schema 1 or 2. Published
migrations and existing rows are preserved. Tests exercise fresh initialization and
upgrade from BS-007 with original v1 snapshot bytes/recording times unchanged.

Scenarios A–M are covered by pure contract and fictional PostgreSQL tests: observed ten
events, exact reviewed scope, later review, contradiction, empty evidence, DateOnly,
DST ambiguity, explicit corrections/rescheduling, complete/partial feature gates,
new immutable snapshots, late evaluation labels and revocation. PostgreSQL 17 tests
require Docker and are not skipped if unavailable. Exact verification results belong
in the PR for its actual head. Recommended BS-009: one permitted completeness/time
provider contract plus outcome provenance and independently verified inventory before
an evaluation executor or model.
