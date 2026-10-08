# Data quality, identity review and reconciliation (BS-006)

[ADR 0019](../../adr/0019-quality-review-reconciliation.md) builds on
[the synthetic ingestion slice](first-football-ingestion.md). No live transport,
candidate policy approval, authentication or public administrative endpoint exists.
BS-005 was merged into main at `3cecf11` before this branch was created.

## Rules and evidence

Domain contains provider-neutral severities, classifications, assessments and gate
results. Application owns the initial football rule catalog and commands/queries;
Infrastructure reads evidence and implements PostgreSQL adapters. No packages or
production project references are added. Rules are deterministic C# functions,
version 1; there is no scripting or runtime configuration framework.

| Rule (`football.` prefix, version 1) | Evidence / reason when blocked |
| --- | --- |
| sport | Supported football context; `unsupported_sport` |
| competition-season | Sport and season/competition relationship; `competition_season_mismatch` |
| event-date | Non-default date-only value; `invalid_event_date` |
| season-interval | Supplied canonical season bounds, if present; `outside_season_interval` |
| different-participants | Two distinct reviewed participants; `same_participants` |
| participant-sport | Football team participants; `participant_sport_mismatch` |
| canonical-identity | Reviewed context and non-ambiguous event; `identity_unresolved` |
| event-collision | Existing event sport/competition/season; `event_identity_collision` |
| home-away | Existing event role assignments; `home_away_conflict` |
| event-status | Conservative status transition graph; `invalid_status_transition` |
| date-conflict | Changed date needs strictly later retrieval evidence; `contradictory_event_date` |
| correction-chain | Same-source/type stream and predecessor version/availability; `correction_chain_invalid` |
| provenance | Source, RAW receipt and retrieval/creation consistency; `provenance_incomplete` |
| replay | Newer evidence or changed frozen target; `older_evidence_replay` |
| cross-source | Different current provider date for the same event; `cross_source_observation_conflict` |
| source-validation | Parser's stable code, including `duplicate_reference` and `identity_collision` |

Passes are recorded as well as issues. A legitimate later date change is
`HistoricalCorrection`, not an automatic conflict. Equal-reference equivalent
rows are `DuplicateEquivalent`; contradictory rows are `DuplicateContradictory`
and neither is automatically published. Other classifications include
`IdentityAmbiguous`, `CanonicalMismatch`, `ObservationConflict`, `InvalidTransition`
and `Superseded`. Cross-source disagreement does not establish which source is wrong.

Info/Warning/Error/Critical severity is independent of analytical blocking.
Unresolved identity is a Warning that blocks eligibility without making source
syntax invalid. Missing season bounds do not fabricate an interval. Completed and
Cancelled are terminal in this initial transition graph; exceptions need an
explicitly reviewed future rule version, not blanket acceptance of latest values.
Source CSV still establishes only Completed or unknown status; other transitions
are tested with provider-neutral rule evidence, without inventing provider facts.

Publication evaluates rules within the existing READ COMMITTED source lock before
writing observations. Unresolved source observations remain frozen without a
canonical target. New validation blockers retain RAW and rule evidence, produce
Partial and cannot publish canonical facts. Legacy identity transaction-denial
semantics remain; explicit reconciliation assesses conflicts durably before
attempting publication. Reused successful batch receipts avoid redundant writes;
explicit reconciliation supplies a new assessment execution for reevaluation.

## Persistence and trust

The additive `20261008094144_DataQualityIdentityReview` migration adds only
`quality.QualityAssessments` and `quality.MaintenanceEvents`, constraints/indexes
and triggers. Previous migrations, RAW/observation clocks and histories remain
unchanged. Assessments identify execution, RAW/source/run, row/reference, optional
identity/observation, policy, rule/version, severity, classification, eligibility
block and fixed reason code. Reconciliation includes a SHA-256 context key.
There are no response bodies or arbitrary diagnostic JSON.

PostgreSQL overwrites recording time on INSERT. Assessment/execution time is
separate. Ordinary UPDATE/DELETE/TRUNCATE and EF mutations are rejected. RAW and
observation source relationships are checked; RESTRICT FKs preserve evidence.
Migration owners can bypass guards, and recording time is not COMMIT time. Use
separate ordinary application and migration/maintenance roles as in ADR 0017.
No automatic startup migration or privileged purge operation is added.
Rolling back this migration drops new quality/maintenance history and is not a
recovery or reset procedure. Preserve evidence and review any owner-driven rollback
explicitly; use fresh disposable databases for development resets.

## Explicit development operator workflow

Configure a disposable PostgreSQL database and the same private RAW directory as
the BS-005 demo, using environment variables `ConnectionStrings__BetStats` and
`Ingestion__RawStoragePath`. Do not include credentials in shell examples or Git.
Run `dotnet tool restore`, then manually apply migrations:

```powershell
dotnet ef database update --project src/BetStats.Infrastructure
dotnet run --project src/BetStats.Worker --configuration Release -- --synthetic-demo --approve-synthetic
```

The following Worker commands use normal .NET configuration switches. Replace
UUID placeholders with values from bounded inspection. They never make network
requests. `Quality:Action` is explicit; ordinary Worker startup has no such action.
Supplied operator identifiers are audit claims, **not authenticated identities**.
Keep these commands restricted to trusted local operators; no HTTP routes exist.

```powershell
dotnet run --project src/BetStats.Worker -- --Quality:Action list --Quality:SourceId <source-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action inspect --Quality:IdentityId <identity-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action candidates --Quality:IdentityId <identity-uuid>
dotnet run --project src/BetStats.Worker -- --Quality:Action review --Quality:SourceId <source-uuid> --Quality:IdentityId <identity-uuid> --Quality:Decision Approve --Quality:TargetKind Participant --Quality:TargetId <reviewed-target-uuid> --Quality:ExpectedVersion 1 --Quality:OperatorId operator:fictional --Quality:Reason "Reviewed fictional fixture"
dotnet run --project src/BetStats.Worker -- --Quality:Action reconcile --Quality:RawId <raw-uuid> --Quality:Competition FICT --Quality:Season 2026-fiction --Quality:OperatorId operator:fictional --Quality:Reason "Reprocess after explicit mapping"
dotnet run --project src/BetStats.Worker -- --Quality:Action report --Quality:ExecutionId <reconciliation-execution-uuid>
```

Candidate lists are bounded, sorted, read-only same-sport entities, not name-match
approval suggestions. Approve requires target existence, correct kind, inferred
sport and expected ledger version. Wrong source fails. Unknown sport context fails
closed: context comes from existing canonical decisions or football assessment/
associated capture evidence, never a caller-supplied sport assertion. Candidate
scope alone does not prove competition/season/role consistency; publication rules
validate that context. Do not use labels as global identifiers.

`Reject` and `Ambiguous` omit TargetKind/TargetId, append Unresolved or Ambiguous,
and audit the explicit action. Reject means reject the currently inspected mapping
proposal; no rejected target becomes a selected canonical identity. Changing an
approved target is another Approve with the current ExpectedVersion and a reason.
PreviousDecisionId/version retain history. Two operators using the same version
get one accepted decision and one `concurrent_review_conflict`, both audited.

Application batch reconciliation accepts 1..20 distinct RAW UUID/context pairs.
Worker exposes the narrower single-RAW form. Reads verify exact SHA-256/length;
current source/permissions are checked before read and under source lock before
assessment. Publication independently rechecks its existing purposes including
DataRetrieval, storage, retention and InternalAnalytics. Restricted attribution/
retention contexts that this workflow cannot satisfy fail closed.

The reconciliation key includes RAW UUID/hash, explicit competition/season,
parser version, rule version and relevant latest identity decision IDs/versions.
It is assessment evidence, not a bypass of existing source/hash/context publication
or target-bound row receipts. Subset processing cannot create a full-payload batch
receipt. Outcomes are AlreadyProcessed, NewlyResolved, StillUnresolved, Conflict,
Rejected or Failed, stably ordered by RAW UUID then row. Old evidence, including
previously unresolved streams, cannot restore facts with newer retrieval evidence.
Changing a frozen target does not silently republish old evidence onto a new event.

Each execution has protected Started and Completed/Partial/Denied/Failed/
Interrupted maintenance records; RAW manifest/storage failures have child audit
records linked to the execution. Quality assessments retain row outcomes. Individual
republication attempts have separate BS-005 run audit and original RAW links; no
new capture or provider retrieval occurs. Partial committed work is retained and
safe to replay. Cancellation propagates after a bounded audit attempt.

For a stopped execution only, after independently confirming ownership ended:

```powershell
dotnet run --project src/BetStats.Worker -- --Quality:Action interrupt --Quality:ExecutionId <execution-uuid> --Quality:OperatorId operator:fictional --Quality:Reason "Owner confirmed stopped"
```

This is not a lease detector. If PostgreSQL is unavailable, terminal audit can fail
and Started remains; manually confirm stopped ownership and record interruption
after recovery. Concurrent interruption closure is not a distributed workflow.

## Historical eligibility

```powershell
dotnet run --project src/BetStats.Worker -- --Quality:Action eligibility --Quality:ObservationId <observation-uuid> --Quality:AsOfUtc 2026-10-08T12:00:00Z --Quality:Purpose InternalAnalytics --Quality:Mode HistoricalAsKnown
```

Use an actual cutoff from your execution. Times require UTC microsecond precision,
cannot be in the future and mode must be explicit. RetrospectiveReconstruction
additionally requires `--Quality:ReconstructionAtUtc <later-utc-time>`.

HistoricalAsKnown filters observation availability/recording and RAW receipt at T;
identity decisions, policy and versioned assessments must also be known by T. It
uses frozen targets and does not join present-day resolutions into old facts.
Superseded facts, missing complete rule evidence, unsupported versions, critical
conflicts or incomplete provenance deny with reasons. New normalization of old RAW
remains invisible before its observation recording time.

RetrospectiveReconstruction keeps the same observation/RAW evidence cutoff but
explicitly uses decisions/assessments/policy available by the separate reconstruction
time. It returns both frozen and interpreted target, decision ID, policy and
assessment IDs. A later reviewed mapping may interpret old evidence without
changing stored observations. It is not a backdating mechanism or a HistoricalAsKnown
answer. A resolved later mapping can lift only the identity-ambiguity blocker;
other conflicts remain blockers. No materialized dataset or snapshot is created.

Both modes also require current operational enablement and current licensing,
requested usage purpose, InternalAnalytics and HistoricalRetention. Retention
age and declared restrictions are checked. Storage/retrieval permission cannot
stand in for dataset usage rights. Cross-provider current-at-T contradictions are
reported as conflicts without identifying a winner. Legacy rows with no complete
quality evidence fail closed until explicitly assessed; history is not backfilled.
The initial complete quality catalog applies to football event metadata. Other
observation categories may lack evidence and are denied rather than guessed usable.

## Reports, inspection and reset

Reports accept an execution UUID or ingestion run UUID, maximum 5000 records;
larger results throw instead of silently truncating. At most 1000 assessments per
eligibility request and 100 cross-source fact candidates are read. Review limits
are 200 identities, 100 associated RAW references and 100 canonical candidates.
Use smaller RAW batches when a report would exceed its bound. Source files retain
the BS-005 1 MiB / 5000 row bounds.

Rows are grouped by RAW/row; reevaluation selects latest recording per rule.
Exclusive outcome priority is invalid, equivalent duplicate, conflict, unresolved,
accepted. Thus Total = Accepted + Unresolved + Rejected, where Rejected = Invalid +
Duplicates + Conflicts. Valid = Accepted + Unresolved is the admissible source-row
count, excluding invalid/duplicate/conflicting rows. Superseded previously accepted
rows can remain in Accepted history while eligibility excludes them.
Validation pass rate = Valid/Total; identity resolution rate = Accepted/Valid;
conflict rate = Conflicts/Total; analytical eligibility rate = Eligible/Total.
Empty rates are zero. Current report eligibility is evaluated separately and
does not overwrite assessments or promise a historical dataset.
Invalid headers with a known parsed row count assign rejection to each source row.
Undecodable/unparseable payloads with no reliable row count are reported separately
as PayloadFailures; they do not fabricate a source record or denominator.

```sql
SELECT "ExecutionId", "RawPayloadId", "Row", "RuleId", "RuleVersion", "Passed",
       "Severity", "BlocksEligibility", "Classification", "ReasonCode", "RecordedAtUtc"
FROM quality."QualityAssessments"
ORDER BY "RecordedAtUtc", "Id" LIMIT 100;
SELECT "ExecutionId", "Action", "OperatorId", "TargetId", "Result", "RecordedAtUtc"
FROM quality."MaintenanceEvents" ORDER BY "RecordedAtUtc", "Id" LIMIT 100;
```

Run automated scenarios with `dotnet test BetStats.slnx --configuration Release`.
Disposable Testcontainers PostgreSQL 17 and temporary RAW roots are created and
removed by tests; no integration test is silently skipped. For manual resets,
use a fresh dedicated disposable database and RAW directory as documented in
BS-005. Never disable history guards or delete retained evidence to rerun the demo.

## Limitations and next scope

Manually supplied operator identity is unauthenticated; database roles and private
storage root are trust boundaries. No point-in-time commit snapshot, distributed
lease, production storage or automated retention process exists. PostgreSQL failure
may prevent outcome audit; reconciliation has a documented operator recovery path.
Legacy inconsistent target contexts require review; proposals alone are not canonical
truth. Rules intentionally fail closed when evidence or rights cannot be established.
No real datasets, HTTP, statistics, odds, ML, frontend or deployment are added.

Recommend BS-007: reviewed dataset assembly contracts and reproducible evidence
manifests (separate historical/reconstruction modes), or a protected operator
interface with authentication in a separately scoped milestone. Keep live provider
selection behind independently verified licensing and explicit policy approval.
