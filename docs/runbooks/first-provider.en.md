# BS-018 provider setup, canary and recovery

**Live activation is blocked.** This milestone delivers the independently testable
API-Football boundary because no reviewed provider grant or operator credential was
supplied. No live command is implemented. Setting environment variables cannot
activate this boundary. Do not approve a source using a mock test result.

## Commands that work now

From the repository in PowerShell:

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx -c Release --no-restore
dotnet test tests/BetStats.UnitTests -c Release --no-build --filter FullyQualifiedName~ApiFootballCanaryTests
dotnet test tests/BetStats.IntegrationTests -c Release --no-build --filter FullyQualifiedName~ApiFootballBoundaryTests
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Web --no-launch-profile -- --urls http://127.0.0.1:5081
```

Open the displayed local URL at `/provider-readiness?lang=en-GB` (or `pl-PL`).
It describes delivery readiness only, without reading a credential, calling a
provider or claiming real quota/observation evidence. The existing BS-017 monitor
and Worker commands inspect actual stored evidence:
[authorized acquisition](authorized-acquisition.en.md),
[frozen prediction/evaluation commands](automated-pipeline.en.md),
[validation commands](real-data-validation.en.md).
Those commands accept existing authorized local captures; they **cannot** invoke
the new API boundary or convert its JSON into a valid CSV acquisition plan.

## Configuration contract for a future authorized activation

Secrets must come from an operator-owned environment/approved secret store, never
command-line values, committed appsettings or output. Proposed environment name:
`BETSTATS_API_FOOTBALL_KEY`; **not currently bound by the application**. Do not dump
the environment to verify it. A future composition root must explicitly read the
key and construct the boundary with Development=true, a dynamic enable predicate,
a reviewed AcquisitionPageContext (source, exact policy UUID/version, scope,
category), validated ApiFootballCanary and one shared RequestBudget per provider.
All four existing import purposes must remain authorized before and after each
request; outer AuthorizedProviderExecutor additionally checks operational source
enablement. No logging handlers may record headers/bodies. The real HTTP handler
must disable automatic redirects, retries and cookies, and enforce cancellation.
This factory and runtime registration are intentionally absent while rights are unknown.

## Approval and canary checklist

1. Review the [decision matrix](../en/data/provider-decision-matrix.md) and actual
   account/product terms; obtain needed third-party rights. Persist a draft policy
   before approving it using existing [governance](../en/data/source-governance.md).
   Do not synthesize policy approvals, grant IDs, publication clocks or identity mappings.
2. Confirm one competition, season and <=7-day UTC window, with actual subscription
   coverage and conservative per-minute/day quota. No paid subscription is purchased
   by this task. RequestBudget is per-process and resets on restart; it is not a
   durable/distributed provider allowance. Resolve this before autonomous dispatch.
3. Implement/review the minimal live RAW-to-canonical child operation on the existing
   BS-015 pipeline. Original JSON must reach durable RAW/context/audit before reader
   interpretation, including malformed responses when storage rights permit. Persist
   exact hash, source/policy, scope, retrieval and database recorded clocks; do not
   treat the current in-memory RetrievedContent as a durable receipt.
4. Reuse reviewed canonical identities and existing source-bound event-time and
   result evidence; corrections append observations. Prove idempotent recovery,
   fencing, concurrent ownership, revocation and schedule-disable behavior against
   disposable PostgreSQL before exposing one explicitly enabled run-once command.
5. Plan/enable/invoke the existing bounded pipeline dispatch only after approval.
   No startup dispatch or second scheduler is allowed. This release has **no live
   enable/acquire command**; do not use curl as a substitute for the governed route.
6. Inspect actual RAW hashes/ledger IDs and BS-017 readiness/cohort reports. Select
   existing PrematchPrediction around 12 hours ahead only when canonical kickoff,
   as-known history, independent coverage and warm-up gates pass. Existing cutoff
   is actual database time, never fixture date or a retrospective requested time.
7. After kickoff acquire independently observed FT evidence through the approved
   operation, inspect current identity/quality/results, then run existing
   PostmatchEvaluation referencing the immutable original prediction. Unknown
   coverage, missing half/full results or unsupported status must block/exclude.
8. Disable the existing job (`Pipeline:Action=disable` with its JobId and operator
   audit approval per the linked runbook), disable the provider switch and revoke
   policy if required. No new provider schedule exists to disable in this release.

## Troubleshooting and recovery

Missing/invalid key or unsupported scope: no HTTP. 401/403: stop, verify account
without logging key/body. 429: honor Retry-After using existing cooldown; no implicit
retries. Timeout/499/5xx: sanitized failure, operator retry only under bounded existing
attempts. Redirect/oversize/non-JSON: reject. Malformed JSON: preserve original RAW
before reader, then reject. Multi-page fixtures: unsupported contract, reduce scope
or review a new documented contract; never append undocumented page parameters.
Duplicate/conflicting fixtures: retain original RAW, explicitly review identity or
correction; never silently deduplicate or overwrite. PST/CANC/TBD: preserve status
and block normal predictions. AET/PEN are not FT labels. Missing UTC seconds or
timestamp disagreement: kickoff unavailable. Unknown policy, unsatisfied attribution
or unsupported retention: fail closed; no temporary bypass.

Interrupted live collection recovery remains unimplemented pending authorization.
Existing local operations recover through stable child IDs and fenced ownership
described in the linked runbooks. Never reset clocks, replay past canaries as real
pre-match evidence, rewrite RAW, delete prediction artifacts or weaken gates to
increase sample counts. No real accuracy/calibration claim is currently available.
