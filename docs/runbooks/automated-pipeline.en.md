# BS-015 operator runbook

This is an explicit Development-only dispatcher. Starting API, Web or Worker does not execute jobs or apply migrations. There are no scheduler administration HTTP endpoints. Operator names and reasons are audit claims, not authenticated identities.

## Prepare

Apply migrations explicitly to an approved PostgreSQL database, using the existing migration procedure. Set `DOTNET_ENVIRONMENT=Development` and `ConnectionStrings__BetStats` through your local secret configuration. Keep RAW storage outside the repository. Use approved, project-authored synthetic inputs when verifying the workflow. No HTTP provider transport is implemented or enabled by this dispatcher.

`PipelineDefinition` has `Version=1`, a `Kind` (`LocalSynchronization`, `PrematchPrediction`, `PostmatchEvaluation`), `Schedule`, `PayloadJson`, `MaximumAttempts` (1–5), `LeaseSeconds` (1–1800) and `PredictionHorizonSeconds` (default 43200). `PayloadJson` is a JSON **string containing an object**, not a nested definition object. Both string and numeric enums are accepted.

`Schedule` requires a UTC `FirstDueUtc`, optional `IntervalSeconds` (60–2592000), `TimeZone="UTC"` and `DaylightSavingPolicy="NotApplicable"`. Use the verified kickoff minus the chosen horizon for prematch due times. Local wall-clock schedules and inferred daylight-saving choices are rejected. Recurrence is anchored to planned instants; missed occurrences are skipped after completion, never replayed as historical executions.

## Plan and operate

Save an approved definition as a local JSON file. For local synchronization obtain the existing BS-013 `FootballHistory:Action=plan` result first, under the approved source policy. Its `Result` is the `Plan` in this payload:

```json
{"Plan":{"Version":1,"SourceId":"<approved UUID>","Scope":{"CompetitionReference":"<reviewed scope>","SeasonReference":"<reviewed scope>"},"Profile":"football-history-v1","Hash":"<approved SHA256>","Length":123,"Fingerprint":"<BS-013 fingerprint>"},"Path":"C:\\approved-inputs\\fiction.csv"}
```

Serialize that payload to the definition's `PayloadJson`. Do not invent hashes, approvals, source identifiers or historical times. Planning the durable job stores a disabled version; it does not synchronize or predict.

For example, after saving the actual BS-013 plan response as `import-plan.json`, construct the definition locally:

```powershell
$plan = (Get-Content C:\approved-inputs\import-plan.json -Raw | ConvertFrom-Json).Result
$payload = @{ Plan = $plan; Path = 'C:\approved-inputs\fiction.csv' } | ConvertTo-Json -Depth 30 -Compress
$planned = [DateTime]::UtcNow
$planned = $planned.AddTicks(-($planned.Ticks % 10))
@{
  Version = 1; Kind = 'LocalSynchronization'; PayloadJson = $payload
  Schedule = @{ FirstDueUtc = $planned.ToString('o'); TimeZone = 'UTC'; DaylightSavingPolicy = 'NotApplicable' }
  MaximumAttempts = 3; LeaseSeconds = 300; PredictionHorizonSeconds = 43200
} | ConvertTo-Json -Depth 30 | Set-Content C:\approved-inputs\job.json -Encoding utf8
```

Round `FirstDueUtc` to microseconds if your clock emits finer precision; definition validation rejects sub-microsecond timestamps. This is a planned dispatch time only; PostgreSQL supplies the actual execution clock.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$job = '<job UUID>'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved synthetic workflow', '--Pipeline:Approve=true')
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=C:\approved-inputs\job.json @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=inspect --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=status @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=disable --Pipeline:JobId=$job @audit
```

`run-once` acquires at most one enabled due job, globally, using PostgreSQL's clock. An empty queue exits successfully without inventing an execution. Invoke the dispatcher explicitly at the desired operational cadence; enabling a job alone starts no background process. Multiple explicitly started dispatchers may acquire different due jobs concurrently. Each execution holds a dedicated unpooled PostgreSQL session lock; heartbeat renewals preserve its bounded lease. Disabling prevents future acquisition; cancel an active execution separately.

For opt-in continuous polling, explicitly invoke `Pipeline:Action=work`, with the same audit approval. `Pipeline:DurationSeconds` is bounded to 1–3600 (default 300), and `Pipeline:MaximumExecutions` to 1–100 (default 10). It polls every second and exits at its deadline, count limit or shutdown. It starts only through this command. A failed, blocked or cancelled execution makes the process exit unsuccessful; retries remain explicit.

Read commands require actor/reason. All mutating commands additionally require `Approve=true`. `inspect` and `status` omit definition payloads, local paths, operator reasons and provider content.

## Prediction and evaluation

Prematch payload: `{"Dataset":<BS-010/011 FootballResultDatasetRequest>,"Predictor":<BS-012 BacktestDefinition with versioned model parameters>}`. The dispatcher replaces dataset and target cutoffs with its actual PostgreSQL acquisition time, builds governed frozen features, checks source-bound minute/second kickoff evidence and the exact scheduled horizon, then publishes through the existing immutable BS-011 operation ledger. Dataset and model hashes, policy versions and feature evidence remain in the child artifact. Operational receipts distinguish planned due time, actual execution and actual feature cutoff. Late execution never uses the planned time as a pretend prediction cutoff.

Existing definitions retain their cutoff-before-target-day rule. Pipeline definitions explicitly select the optional versioned metadata policy `TargetTimePolicy="source-bound-kickoff-v1"`. It allows a same-day target only with one minute/second source-bound kickoff, known and recorded by cutoff, whose resolved UTC date matches the target's UTC calendar date. The 12-hour default therefore also supports same-day targets with admissible evidence. Model history still excludes all same-day/future results. Missing or conflicting kickoff, unknown history and denied rights block publication; they are not repaired by invented data or retrospective interpretation. Publication frozen after kickoff cannot be presented as an operational prematch prediction. A blocked job may need an explicitly revised/new approved definition.

Postmatch payload: `{"PredictionExecutionId":"<completed pipeline execution>","BacktestId":"<frozen prediction artifact>","BacktestHash":"<exact SHA256>"}`. Plan it after the prematch artifact is known. The prediction must have been genuinely frozen before its verified kickoff. The dispatcher replays frozen prediction values and model provenance; it does not run a new model to evaluate them. Existing v3 eligibility checks require independent complete result coverage, source-bound ends, precise times and current rights. Missing evidence yields exclusions and unavailable metrics. Nothing certifies provider completeness or operational accuracy automatically.

## Cancel, retry and recover

```powershell
$execution = '<execution UUID from inspect/status>'
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=cancel --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=retry --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release -- --Pipeline:Action=recover --Pipeline:ExecutionId=$execution @audit
```

Cancellation is durable and cooperative. Child artifacts already committed remain immutable; a cancellation receipt does not erase them. Retry is for failed/blocked/cancelled executions without a published pipeline output; recovery is for an expired running execution with no live owner session. Both consume the same bounded attempt budget and retain the original request fingerprint and planned occurrence. A live session cannot be overtaken merely because its lease expires. Definitions cannot be revised while their job is running.

Local retries reread the exact approved bytes and recheck current authorization, retention, capabilities, budget and identity review. Restoring a file does not approve its identities. If prediction publication already succeeded before a crash, recovery reuses the frozen child artifact. An interrupted prediction operation that never completed is blocked with `new_job_required_for_unpublished_prediction`: plan a new current-cutoff job rather than impersonating an earlier run. A new feature stage uses the current attempt's actual clock. Existing pipeline outputs cannot be overwritten by retry.

On shutdown the worker attempts a bounded cancelled receipt. If the database or fence is unavailable, it leaves the durable running state for explicit recovery. Never infer success from a terminated process or a missing terminal receipt. Inspect the existing child operation ledger before deciding how to proceed.

## Observe

Status distinguishes PostgreSQL infrastructure availability from `ProviderDataReadiness=NotCertified`; infrastructure failure is reported without provider readiness claims. Check state, lease, owner token, attempt count, last success, execution timestamps and receipt categories. Correlation IDs are execution UUIDs. Structured process logs contain identifiers and sanitized categories, never RAW bytes, local payload paths, credentials or provider content. Configure your external console collector's retention separately.

The SQL-managed `pipeline.Diagnostics` table retains at most 1000 sanitized completion records, pruning entries older than 30 days on every completion. It is separate from the immutable audit ledger; an idle database performs no automatic maintenance. Immutable definitions, receipts and artifacts are not operational logs and are retained for provenance. GET APIs and Development-only dashboard restrictions remain unchanged.
