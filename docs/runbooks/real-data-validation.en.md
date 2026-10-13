# BS-016 operator workflow (Development only)

## Prerequisites and limits

Use .NET 10, the locked dependencies and an explicitly configured PostgreSQL
database migrated manually using the existing persistence runbook. Set
`DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats` and
`Ingestion__RawStoragePath`. Keep credentials in environment configuration, never
in definitions, source files or reports. Actor/reason are audit claims, not login.
No command runs at startup. No provider HTTP or scraping is implemented.

Obtain an operator-supplied file and independently review its source permissions.
Use BS-013 [local-history instructions](../en/data/real-football-history.md)
and the existing identity review/reconciliation commands. Approved profiles are
`football-history-v1`, `results-v1`, `metadata-v1`; no automatic conversion or
guessing. Existing SourcePolicy approval does not establish contractual rights.
RAW must retain its exact hash, original profile and competition/season scope.
Every row must pass existing canonical identity/quality review. Imported dates
are not kickoff instants; source publication times never replace observation time.

Validation bounds: one RAW object <=1 MiB, <=500 parsed rows, <=2000 parse issues,
and <=3 distinct prediction/evaluation pairs. Oversize input fails; it is never
silently truncated. Existing linked artifact limits and 20000 RAW identifiers per
manifest also apply. Validation references the complete original cohorts, not a
selected success subset. No ranking across models is performed.

## Inspect, execute, retrieve, verify

Create an approved `validation-input.json` (UUID/hash placeholders must be replaced
with actual immutable records; no real dataset is bundled):

```json
{
  "Version": 1,
  "SourceId": "<source UUID>",
  "RawId": "<existing authorized RAW UUID>",
  "ExpectedRawHash": "<lowercase SHA256 of original bytes>",
  "Profile": "football-history-v1",
  "Scope": { "CompetitionReference": "E0", "SeasonReference": "2020-2021" },
  "EvidenceKind": "OperatorDeclaredReal",
  "Publications": []
}
```

Use `Synthetic` for fictional tests and `Unknown` if origin is unestablished.
`OperatorDeclaredReal` records an operator claim, not independent origin proof.
Get RAW identifiers from the authorized ingestion metadata audit (a read-only
operator SQL query is `SELECT "Id", "ContentHashSha256", "ExternalReference"
FROM ingestion."RawPayloads" WHERE "DataSourceId" = '<source UUID>'::uuid;`).
Do not expose storage keys, original bytes or policy evidence in shared reports.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
# Set ConnectionStrings__BetStats and Ingestion__RawStoragePath securely first.
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved bounded dataset validation', '--Pipeline:Approve=true')
$inputPath = 'C:\approved-inputs\validation-input.json'
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=inspect-readiness --Pipeline:ValidationInputPath=$inputPath @audit
$inputObject = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
$definition = @{
  Version = 1; Kind = 'FootballValidation'
  Schedule = @{ FirstDueUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); TimeZone = 'UTC'; DaylightSavingPolicy = 'NotApplicable' }
  PayloadJson = ($inputObject | ConvertTo-Json -Depth 30 -Compress)
  MaximumAttempts = 3; LeaseSeconds = 300; PredictionHorizonSeconds = 43200
}
$definitionPath = 'C:\approved-inputs\validation-job.json'
$definition | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $definitionPath -Encoding utf8
$job = [Guid]::NewGuid().ToString()
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=$definitionPath @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=inspect --Pipeline:JobId=$job @audit
$execution = '<execution UUID from inspect>'
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=validation-report --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=verify-validation --Pipeline:ExecutionId=$execution @audit
```

`inspect-readiness` performs explicit deep model verification only if publications
are supplied; it does not store reports or execute a new forecast. `run-once`
acquires one enabled due job globally: inspect its JobId; do not assume it selected
your job if other jobs are enabled. Cancellation/retry/recover and bounded work
use the existing [BS-015 runbook](automated-pipeline.en.md). A completed validation
receipt means inspection finished. Check the separate report Readiness and issues.
Repeated dispatch cannot duplicate prediction publications because validation
only inspects them. Reuse the same job/definition; a new job is a new inspection.

## Prediction and evaluation evidence

First run an approved BS-015 `PrematchPrediction` job for each desired existing
BS-012 Elo/Poisson/Dixon-Coles model. This requires independently known precise
kickoff, reviewed identities, current rights and historical as-of features. The
actual database acquisition time is the prediction cutoff. Never substitute the
planned cutoff or backdate recording. A historical CSV imported now cannot prove
pre-event knowledge; missing inputs remain blocked/excluded.

After genuine result availability, execute existing `PostmatchEvaluation` against
the previously frozen prediction. Add each completed pair to `Publications`:
`[{"PredictionExecutionId":"<UUID>","EvaluationExecutionId":"<UUID>"}]`.
Omit the optional evaluation UUID while no eligible postmatch output exists.
The referenced model must actually consume the selected RAW in the same scope.
Unrelated artifacts are rejected. Validation deeply replays existing models and
frozen evaluation metrics without generating a new prediction publication.
It preserves sample denominators, warm-up exclusions, unavailable calibration,
null metrics and positive-infinity semantics. No model accuracy is certified.

The report keeps result event dates, optional source publication, retrieval,
availability and database recording times separately, plus planned/actual
prediction cutoff, execution/publication and evaluation cutoff for linked models.
CSV kickoff precision remains uncertified; precise evidence belongs to linked
BS-015 frozen artifacts. Recovery is tested in synthetic software scenarios;
an ordinary inspection does not prove recovery occurred for the operator dataset.

## Dashboard and retention

With current display rights, open `/validation/<execution UUID>?lang=pl-PL`
or `?lang=en-GB` in the existing local Web dashboard. The underlying GET
`/api/v1/dashboard/validation/<UUID>` requires Development and loopback, sets
no-store and checks current rights, retention and RAW hashes. It never dispatches
jobs, imports, predicts or evaluates. Demo mode returns no real report.
Model/quality views continue to use existing endpoints. Source attribution
requirements fail closed because this workflow does not assert attribution.
Do not retain exported reports beyond approved rights. Use the existing privileged
retention procedure for immutable database artifacts; there is no new purge bypass.

Without authorized data, use only explicitly labeled synthetic fixtures and run
software tests. Real-data origin, provider compatibility, empirical calibration
and operational accuracy remain unavailable. BS-017 should establish an approved
dataset acquisition/archival contract with independently supported as-of evidence,
complete result coverage and a prospective evaluation cohort before model tuning.
