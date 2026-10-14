# BS-019A preparation verification — incomplete delivery

Baseline: `origin/main` `6d50344e82c3824d7dcf54c1c8ddbdefe9008ef6`.
BS-018 PR #22 confirmed merged. Branch: `feat/bs-019a-durable-provider-publication`.
See [audit](bs-019a-audit.md) and [proposed ADR 0033](../../adr/0033-durable-provider-publication.md).

## Delivered preparation

Unregistered pure source-to-domain mapping over the existing BS-018 reader, exact
RAW hash/context validation, source-scoped reference format, explicit status mapping,
independent nullable final/half-time score handling, deterministic replay and cancellation.
43 new controlled contract cases. PL/EN status/runbooks and mapping contract documentation.

No runtime profile registration, canonical database publisher, durable publication
receipt, mutation command, new read API/dashboard, schema migration or HTTP activation.
No source grant, real data readiness, predictions or accuracy claims. Existing operator
Web launch settings are excluded. Frozen artifacts and historical RAW are untouched.

## Acceptance and test limits

| Area | Evidence/status |
| --- | --- |
| Scope/provider/profile/hash/clocks | Pure mapping rejection tests; database authority still required |
| Status progression/postponement/cancellation | Mapping tests; no durable observations published |
| Final/missing/corrected scores | Mapping tests with nullable half-time; no durable correction chain demonstrated |
| Duplicate IDs/members, corrupt JSON, pagination | Existing strict reader reused and exercised |
| Rescheduling/replay/cancellation | Pure mapping tests only |
| Canonical identities and conflicts | Designed in audit; transactional enforcement pending |
| Rights revocation/retention/attribution | Existing governance unchanged; publisher integration pending |
| Atomic receipts/rollback/concurrent owners/fencing | Not implemented or certified for JSON |
| Crash recovery and retry exhaustion | Existing pipeline unchanged; JSON integration pending |
| Historical as-of/frozen predictions/prospective eligibility | Existing regression only; JSON integration pending |
| Read-only receipt monitoring and PL/EN UI | Not implemented; PL/EN documentation only |
| Legacy compatibility | No legacy profile, key, model or artifact changes |

This preparation does **not** satisfy BS-019A acceptance criteria. A draft PR must remain
unmerged until the transactional vertical slice and the complete PostgreSQL failure matrix
are implemented and reviewed. Do not move missing BS-019A requirements into BS-019B.
BS-019B should address explicitly authorized live transport/canary only after BS-019A passes.

## Verification

Executed locked restore and Release build (zero warnings/errors). EF reports no pending
model changes. Full regression uses the existing disposable PostgreSQL 17 Alpine
Testcontainers fixture; no operator database or provider endpoint is used.

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger 'trx;LogFilePrefix=bs019a-preparation' --results-directory .artifacts/bs019a-preparation
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
git diff --check
```

Final test counts and exact-head CI/CodeQL results are recorded after completion; prior
BS-018 checks are not substitutes for BS-019A verification.

## Security

No credentials or provider payload downloads. Mapping validates declarations but cannot
prove a policy is current or a RAW UUID belongs to a source; its documentation explicitly
requires independent PostgreSQL checks by the future publisher. No DI registration or
caller has been added, so the existing fail-closed publication boundary remains in force.
