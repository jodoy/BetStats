# BS-017 authorized acquisition (Development only)

## Prepare

Use .NET 10, a manually migrated PostgreSQL database and a permitted RAW directory.
Set `DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats` and
`Ingestion__RawStoragePath` securely. Never commit credentials or proprietary files.
Review the [source authorization checklist](../en/data/football-source-authorization.md)
and existing [BS-013 instructions](../en/data/real-football-history.md) first.
Identity/source approval is not seeded for real providers. Actor/reason are audit
claims, not authenticated identities. No job or migration starts automatically.

BS-017 supports local CSV using the exact existing `football-history-v1` profile,
and source-bound kickoff JSON using existing `EventTimeSourceClaim` v1. Each file
is <=1 MiB; acquisition reports support <=500 CSV rows without truncation.
API transport remains disabled. Controlled adapter tests use no provider URLs.

## Plan and capture CSV

Commands run from the repository root in PowerShell 7 after Release build. Replace
UUID/path/scope placeholders with independently approved operator configuration.
The plan reads and hashes the file only after current rights checks; interpretation
happens after RAW capture. Category is explicit: HistoricalResults, PrematchMetadata
or PostmatchResults. Use `Synthetic` exclusively for fictional software tests,
`OperatorDeclaredReal` for an operator-declared real file, and `Unknown` otherwise.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
# Configure ConnectionStrings__BetStats and Ingestion__RawStoragePath securely.
$source = '<enabled authorized source UUID>'
$path = 'C:\approved-inputs\fixtures.csv'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved source acquisition', '--Pipeline:Approve=true')
$quiet = @('--Logging:LogLevel:Default=None')
$planOutput = dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=acquisition-plan --Pipeline:SourceId=$source --Pipeline:PayloadPath=$path --Pipeline:Competition=E0 --Pipeline:Season=2026-2027 --Pipeline:Category=PrematchMetadata --Pipeline:EvidenceKind=OperatorDeclaredReal @audit @quiet
if ($LASTEXITCODE -ne 0) { throw 'Acquisition planning failed' }
$plan = ($planOutput | Out-String | ConvertFrom-Json).Result
$inputObject = @{ Plan = $plan; Path = $path }
$definition = @{
  Version = 1; Kind = 'FootballAcquisition'
  Schedule = @{ FirstDueUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); TimeZone = 'UTC'; DaylightSavingPolicy = 'NotApplicable' }
  PayloadJson = ($inputObject | ConvertTo-Json -Depth 40 -Compress)
  MaximumAttempts = 3; LeaseSeconds = 300; PredictionHorizonSeconds = 43200
}
$definitionPath = 'C:\approved-inputs\acquisition-job.json'
$definition | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $definitionPath -Encoding utf8
$job = [Guid]::NewGuid().ToString()
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=$definitionPath @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=inspect --Pipeline:JobId=$job @audit
$execution = '<acquisition execution UUID from inspect>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=acquisition-inspect --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=acquisition-readiness --Pipeline:ExecutionId=$execution @audit
```

`run-once` selects one enabled due job globally. Verify its JobId if other jobs
exist. Existing bounded `work`, cancel, retry and recover commands are documented
in [BS-015](automated-pipeline.en.md). CSV capture delegates to the existing fenced
import operation. A partial receipt preserves RAW and missing identity evidence;
use existing explicit identity review and `FootballHistory:Action=reconcile` with
its RawId/scope/operator approval. Then plan a new inspection/capture if needed.
`Completed` means acquisition inspection completed, not that its report is Verified.

## Acquire kickoff evidence

Do not infer UTC/timezone from CSV Time. Obtain an authorized source claim file:

```json
{
  "Version": 1,
  "OriginalRawId": "<RAW associated with the exact EventDate observation>",
  "ProviderEventReference": "<exact source-scoped event reference>",
  "Context": { "CompetitionReference": "E0", "SeasonReference": "2026-2027" },
  "Value": { "LocalDate": "2026-10-20", "LocalTime": "18:00:00", "TimeZoneId": null,
    "OffsetMinutes": 0, "SourceUtcInstant": null, "Precision": "Minute" }
}
```

The time above is only a format example, not a supplied real match. Use actual
source-supported values. A UTC source instant or unambiguous source timezone/offset
is required by existing EventTimeRules. Unknown precision stays unknown. This v1
claim has no independently supported publication timestamp, so publication remains
null; file timestamps are not substituted.

Obtain the exact DateObservationId, original RawPayloadId and provider reference
from authorized provenance inspection. A bounded operator SQL example is:

```sql
SELECT o."Id", o."RawPayloadId", i."ExternalId", o."AvailableAtUtc", o."RecordedAtUtc"
FROM provenance."Observations" o JOIN provenance."ProviderIdentities" i
ON i."Id" = o."ProviderIdentityId"
WHERE o."DataSourceId" = '<source UUID>'::uuid AND o."Type" = 'EventDate'
ORDER BY o."RecordedAtUtc" DESC, o."Id" LIMIT 100;
```

Run acquisition-plan with the same source/scope plus
`--Pipeline:Category=KickoffEvidence --Pipeline:DateObservationId=<UUID>` and
`--Pipeline:PayloadPath=C:\approved-inputs\kickoff.json`. Build/approve/dispatch the
same FootballAcquisition job shape using this returned plan. An intentional same-
stream correction additionally supplies `Pipeline:CorrectsKickoffId=<prior UUID>`.
The original file is captured before JSON parsing; malformed/wrong-identity claims
remain rejected even though their permitted RAW survives. Recovery reuses its
stable child capture and event-time evidence, with original recording clocks.
Keep the approved file available for retry/recovery; changed bytes require a new plan.

## Predict, reconcile and freeze a cohort

Use existing [BS-015 prematch/postmatch jobs](automated-pipeline.en.md) explicitly.
Acquisition never runs a predictor directly. Missing history/coverage/kickoff and
warm-up are not bypassed. Acquire results as PostmatchResults and use existing
reconciliation and source/result coverage/end evidence before evaluation. Freeze
BS-016 validation as described in its [runbook](real-data-validation.en.md).

Create a new job with Kind `ProspectiveCohort` and PayloadJson containing:

```json
{
  "Version": 1, "SourceId": "<source UUID>",
  "AcquisitionExecutions": ["<completed acquisition execution UUID>"],
  "ValidationExecutions": [], "CoverageQueries": []
}
```

Use the same explicit Pipeline plan/enable/run-once commands above. Add up to three
completed BS-016 validation executions when actual frozen predictions/evaluations
exist; they must bind this acquired source/RAW and consistent origin classification.
CoverageQueries uses the existing CoverageQuery shape, capped at two exact-source,
scope/category/interval queries with HistoricalAsKnown, null reconstruction,
InternalAnalytics and default UsageContext. Cohort assessment replaces AsOfUtc
with its real database clock; no future/historical recording is invented.
An empty list leaves coverage Unknown. Existing owned inventory certificates are
synthetic only; unsupported real completeness is not certified.

Retrieve with `--Pipeline:Action=prospective-report --Pipeline:ExecutionId=<UUID>`.
See the [prospective guide](../en/data/prospective-evidence.md) for count semantics,
calibration/exclusions, immutable cohorts and clock distinctions.

## Monitor and limits

Existing local Web routes:
- `/monitor/acquisition/<execution UUID>?lang=pl-PL`
- `/monitor/acquisition-status/<job UUID>?lang=en-GB`
- `/monitor/prospective/<cohort execution UUID>?lang=pl-PL`

GET projections use `/api/v1/dashboard/acquisition/<UUID>`, `/acquisition-status/<UUID>`
and `/prospective/<UUID>`. They require current display rights, Development and
loopback, set no-store and never acquire/import/predict/evaluate. DEMO does not
fabricate acquisition outputs. Status returns at most 100 sanitized execution heads.
Reports preserve up to 500 rows; cohorts reference <=10 acquisitions/3 validations/
2 coverage queries. Existing BS-015 attempt, lease, diagnostics and artifact limits
remain authoritative. Rights revocation, corruption and expired coverage block reads.
Attribution restrictions fail closed; capped retention unsupported by ingestion is
blocked. Exports/backups remain subject to approved retention procedures.

Without real authorized files, permissions and a reviewed provider-specific adapter,
only labeled synthetic software verification is possible. No real-world accuracy,
provider compatibility or independent completeness is claimed. BS-018 should
review one concrete provider contract, legal/configuration evidence, response
schemas and independent inventory/as-of proof before enabling transport.
