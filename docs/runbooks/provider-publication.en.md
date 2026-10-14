# BS-019A provider publication runbook

Use Development only, an explicitly approved PostgreSQL connection and RAW directory
outside the repository. Apply migration `20261014115707_DurableProviderPublication`
explicitly using the existing migration procedure. Startup never migrates or dispatches.
The workflow accepts existing immutable `api-football-fixtures-v1` RAW only. No HTTP
transport, provider credential, new acquisition or recapture is used.

## Plan and dispatch

Use actual RAW/source/policy records. The example league/season below is fictional;
it is not an entitlement. Actor/reason are audit claims, not authenticated identities.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$raw = '<existing authorized RAW UUID>'
$audit = @('--Pipeline:Actor=operator:local', '--Pipeline:Reason=Approved original RAW publication', '--Pipeline:Approve=true')
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-plan --Pipeline:RawId=$raw --Pipeline:League=999999 --Pipeline:Season=2026 --Pipeline:From=2026-10-13 --Pipeline:To=2026-10-19 --Pipeline:EvidenceKind=Synthetic @audit
```

Save the actual JSON response to `C:\approved-inputs\publication-plan.json`. Do not
invent hashes, policy versions, source clocks or origin classifications. The response
`Result` is the publication input. Construct a BS-015 definition:

```powershell
$input = (Get-Content C:\approved-inputs\publication-plan.json -Raw | ConvertFrom-Json).Result
$due = [DateTime]::UtcNow
$due = $due.AddTicks(-($due.Ticks % 10))
@{
  Version = 1; Kind = 'ProviderPublication'
  PayloadJson = ($input | ConvertTo-Json -Depth 30 -Compress)
  Schedule = @{ FirstDueUtc = $due.ToString('o'); TimeZone = 'UTC'; DaylightSavingPolicy = 'NotApplicable' }
  MaximumAttempts = 3; LeaseSeconds = 300; PredictionHorizonSeconds = 43200
} | ConvertTo-Json -Depth 30 | Set-Content C:\approved-inputs\publication-job.json -Encoding utf8
$job = [Guid]::NewGuid().ToString()
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=plan --Pipeline:JobId=$job --Pipeline:DefinitionPath=C:\approved-inputs\publication-job.json @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=enable --Pipeline:JobId=$job @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=run-once @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-inspect --Pipeline:JobId=$job @audit
```

`run-once` acquires one globally due enabled job, not a job selected by this shell.
Enabling alone starts nothing. The bounded opt-in BS-015 `work` command is also supported.
All mutation requires explicit approval. Plans/inspection/verification require actor and
reason but do not mutate. Limits: RAW 1 MiB, one provider page, 500 fixtures; attempts 1-5,
lease 1-1800 seconds. Large batches must fit the explicitly selected lease; expiry rolls
back publication. Status returns at most 100 receipts, executions and ledger events.

## Review, inspect and recover

All canonical competition, season, home, away and event targets must already have approved
bindings. The publisher never matches names or invents canonical targets. Unknown anchors
are registered with original RAW provenance and a ReviewRequired receipt. Use existing
quality commands to inspect and explicitly review each target:

```powershell
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Quality:Action=list --Quality:SourceId='<source UUID>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Quality:Action=review --Quality:IdentityId='<identity UUID>' --Quality:SourceId='<source UUID>' --Quality:Decision=Approve --Quality:TargetKind=Participant --Quality:TargetId='<reviewed canonical UUID>' --Quality:ExpectedVersion=1 --Quality:OperatorId=operator:local --Quality:Reason='Explicit exact reviewed binding'
$execution = '<execution UUID from inspect>'
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=retry --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=recover --Pipeline:ExecutionId=$execution @audit
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-publication-verify --Pipeline:ReceiptId='<receipt UUID from inspect>' @audit
```

Use `retry` for blocked/failed/cancelled attempts after resolving their cause. Use `recover`
only for an expired Running execution with no live owner session. Both consume the original
attempt budget. Exhaustion records Blocked without work. Recovery always uses the original
RAW UUID/hash/clocks; it never reopens a provider file or issues a provider request.
Changed reviewed decision versions permit deterministic new receipts. A forward correction
of an already published event target requires newer RAW, preserving historical targets.

Canonical rows plus authoritative receipt commit atomically. Parent pipeline acknowledgment
is a separate stage: its failure does not erase an already committed child. After a crash
between stages, recovery reuses the receipt. Read verification checks persisted state using
an independent connection. If rights/RAW are unavailable, inspection fails closed rather
than inventing success. Source revocation and publication are ordered by the shared source
row lock; a revocation committed first prevents publication.

## Read-only monitoring and evidence

Development-loopback GET resources (current display rights required):
`/api/v1/dashboard/publication/<receipt UUID>` and
`/api/v1/dashboard/publication-status/<job UUID>`. Web views:
`/monitor/publication/<receipt UUID>?lang=en-GB` and
`/monitor/publication-status/<job UUID>?lang=pl-PL` (both languages supported).
No GET dispatches, imports, repairs, recovers, predicts or evaluates. POST is unavailable.
Storage paths and provider JSON are excluded from projections.

BS-016 validation accepts profile `api-football-fixtures-v1` with the original RAW/hash/scope
and explicit origin. BS-017 acquisition/cohort reports can reference completed publication
execution UUIDs. Coverage, warm-up, as-of, precise time and frozen-before-kickoff gates remain
mandatory. Parsing interprets JSON; RAW capture preserves bytes; publication records reviewed
observations; prospective eligibility requires independent evidence; frozen prediction
publication and post-match evaluation are separate existing workflows. Synthetic or
operator-declared origin is never independent proof of real-world compatibility/accuracy.

See [transaction ADR](../adr/0033-durable-provider-publication.md),
[failure matrix](provider-publication-recovery.md) and [verification](../en/quality/bs-019a-verification.md).
