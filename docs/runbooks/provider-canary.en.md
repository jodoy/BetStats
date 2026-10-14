# Authorized Development provider canary

Complete the [checklist](../en/data/provider-activation-checklist.md) and
[configuration](../en/data/api-football-configuration.md) first. Commands below are
operator instructions, not approval to execute real HTTP during implementation.
Use a dedicated Development database/RAW store. No automatic migration or startup
acquisition. Never point tests at a real provider.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Explicit approved bounded provider canary', '--Pipeline:Approve=true', '--Logging:LogLevel:Default=None')
$grantPath = 'C:\approved-inputs\provider-grant.json'
$grant = Get-Content $grantPath -Raw | ConvertFrom-Json
$source = $grant.SourceId
$grantId = $grant.Id
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-register --Pipeline:GrantPath=$grantPath @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-status --Pipeline:SourceId=$source @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-plan --Pipeline:GrantId=$grantId @audit > C:\approved-inputs\provider-plan.json
```

Registration records the reviewed grant but disables the source's provider gate.
Inspect the actual stored-plan hash against the original operator grant and confirm
all approval conditions. Set the key/enable environment only as described in the
configuration guide. After explicit approval of this live canary:

```powershell
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-activate --Pipeline:GrantId=$grantId @audit
$input = (Get-Content C:\approved-inputs\provider-plan.json -Raw | ConvertFrom-Json).Result
$due = [DateTime]::UtcNow
$due = $due.AddTicks(-($due.Ticks % 10))
@{
  Version=1; Kind='ProviderAcquisition'; PayloadJson=($input | ConvertTo-Json -Depth 20 -Compress)
  Schedule=@{ FirstDueUtc=$due.ToString('o'); TimeZone='UTC'; DaylightSavingPolicy='NotApplicable' }
  MaximumAttempts=3; LeaseSeconds=300; PredictionHorizonSeconds=43200
} | ConvertTo-Json -Depth 30 | Set-Content C:\approved-inputs\provider-job.json -Encoding utf8
$job = [Guid]::NewGuid().ToString()
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=C:\approved-inputs\provider-job.json @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=inspect --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-status --Pipeline:SourceId=$source @audit
```

`run-once` acquires one globally due enabled job. Check existing job status first;
use an isolated operator canary database with no unrelated enabled due jobs. A
recurring schedule is unnecessary: use a one-time schedule without IntervalSeconds.
Enabling alone performs no work. Acquisition reserves one request durably, captures
original bytes and prepares a **disabled** publication job. Acquisition Completed
means RAW/preparation completed; it does not mean canonical or prediction eligibility.

Read the actual `PublicationJobId` from provider-status. Enable and dispatch it:

```powershell
$publication = '<actual prepared publication job UUID>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=enable --Pipeline:JobId=$publication @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-inspect --Pipeline:JobId=$publication @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-verify --Pipeline:ReceiptId='<actual receipt UUID>' @audit
```

For ReviewRequired use the exact existing [identity review/publication commands](provider-publication.en.md)
and retry the **publication execution**. No provider download is needed. Verify
RAW UUID/hash/length/profile/scope, retrieval and DB recording clocks, each identity
decision and canonical receipt. Source publication time stays null unless independently
supported; current historical downloads cannot become historical pre-match evidence.

For final-result follow-up obtain a new exact approved PostmatchResults grant/window
if the current grant does not cover that category. The source lifetime usage is
retained. Dispatch a new bounded acquisition job; never silently expand scope or
retry HTTP in a recovery command. BS-019A appends rescheduling/result corrections.
Use existing [BS-015 pipeline](automated-pipeline.en.md) prediction/evaluation commands and
BS-017 prospective reports with the publication execution UUID. Frozen artifacts
and original evaluation cohorts are never rewritten. Missing metrics remain unavailable.

Read-only views: `/api/v1/dashboard/provider-activation/<source UUID>` and
`/monitor/provider-activation/<source UUID>?lang=en-GB` (also pl-PL). Current display
rights and Development loopback are required. GET never acquires or mutates.

Finish with [safe disable](provider-canary-recovery-security.md). Keep a local report
containing actual permission/entitlement references, grant/policy/hash, exact scope,
request ledger, RAW hash/clocks, reviewed bindings, verified receipt IDs/hashes,
budget before/after, origin and honest prediction/cohort blockers. Do not commit
provider payloads, document contents or credentials. A controlled test report is
Synthetic; it is never real-canary acceptance.
