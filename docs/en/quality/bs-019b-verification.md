# BS-019B verification

## Final local verification — 2026-10-15

**991 passed, 0 failed, 0 skipped**: 372 unit, 28 architecture and 591 disposable
PostgreSQL 17 integration cases. Integration completed in **6 min 30 s**. A separate
focused run passed **86/86**, with no failures or skips, in **49 s**; these cases
are a repeated subset of the 991, not additional unique cases. This retains the
previous 929 cases and adds 62 (14 unit, 2 architecture, 46 integration).

| Required check | Actual result |
| --- | --- |
| `dotnet restore BetStats.slnx --locked-mode` | Passed; lock files unchanged |
| `dotnet build BetStats.slnx --configuration Release --no-restore` | Passed; 0 warnings, 0 errors |
| `dotnet test BetStats.slnx --configuration Release --no-build` | 991/991 passed |
| `dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure` | No model changes since the last migration |
| `git diff --check` and `git diff origin/main...HEAD --check` | Passed |
| Exact changed-file manifest | 50/50 paths match main-relative changes |

Verified runtime and strengthened test revision:
`b3641b2e86788511e4499f217010944b0e48a1d7`, on the existing
`feat/bs-019b-authorized-provider-activation` branch. The only code change in final
verification strengthens existing test assertions; runtime implementation is
unchanged from `f5550a71b383f5b4a84f877041879fc9476cf87b`.

Workflow evidence for the verified source revision:
[CI #69](https://github.com/jodoy/BetStats/actions/runs/38660075106) and
[CodeQL #68](https://github.com/jodoy/BetStats/actions/runs/38660074843).
The previous implementation head also passed
[CI #68](https://github.com/jodoy/BetStats/actions/runs/38580938464) and
[CodeQL #67](https://github.com/jodoy/BetStats/actions/runs/38580938528);
its CI logs independently confirm 372 + 28 + 591 passing cases and zero warnings/errors.

This report is delivered in a subsequent documentation-only commit. Its final SHA
and exact-head workflow URLs are pinned in the description of
[PR #24](https://github.com/jodoy/BetStats/pull/24), with current conclusions under
[PR #24 checks](https://github.com/jodoy/BetStats/pull/24/checks). Earlier green runs
do not substitute for that final-head check. Keep Draft until both final-head CI
and CodeQL pass; then mark ready for review without merging.

Operational status remains **LIVE_CANARY_BLOCKED**. No real provider HTTP was
executed and no operator-specific provider authorization is claimed.

## Independent security and recovery verification

Inspected the actual source lock/fence/rights boundaries, immutable event schema,
deferred SQL trigger, original-RAW lookup, parent acknowledgment and test assertions.
The separate 86-case run includes ProviderActivationWorkflowTests (45),
ProviderActivationMigrationTests (1), ProviderPublicationWorkflowTests (39), and
PersistenceTests.Fresh_database_is_migrated_and_has_no_pending_model_changes (1).

- Concurrent contexts reserve against one durable source ceiling; restart and
  reactivation preserve usage. The controlled handler records exactly one request.
- A separate governance context revokes the source policy while the controlled
  HTTP response is in flight. The result is PermissionDenied/Blocked, no RAW is
  captured, the reservation remains consumed, and another invocation cannot issue
  a second request. Provider disable in flight and stale ownership also block capture.
- Deferred commit tests assert the exact PostgreSQL rejection messages for expired
  ownership, unavailable rights and exhausted durable quota, rather than accepting
  an unrelated generic constraint failure.
- A lost HTTP response without RAW stays uncertain and cannot trigger automatic
  re-download. Explicit reconciliation never refunds the reservation.
- After RAW capture-before-finalization/ack interruption, recovery verifies and
  reuses the original run-bound bytes/hash/clocks. After canonical commit-before-
  acknowledgment, the real Worker recovery reuses the immutable publication receipt.
- Reviewed identity reprocessing, revised-child conflicts, result/schedule
  corrections, historical cutoffs and frozen artifacts preserve existing evidence.
- Fresh database creation applies all 16 migrations. Upgrade from BS-019A preserves
  populated RAW, canonical observations and publication receipts. Legacy frozen
  prediction/result/evaluation preservation tests remain in the full suite.
- Controlled handlers and synthetic-only fixtures are used throughout. Stock
  transport cannot activate from synthetic grants or synthetic policy evidence.

## Scope and trust boundaries

[Audit](bs-019b-audit.md), [ADR 0034](../../adr/0034-authorized-provider-activation.md),
[configuration](../data/api-football-configuration.md), [activation checklist](../data/provider-activation-checklist.md),
[EN](../../runbooks/provider-canary.en.md) / [PL](../../runbooks/provider-canary.pl.md)
commands and [quota/recovery/security](../../runbooks/provider-canary-recovery-security.md).

One gated Development API-Football fixtures transport, immutable operator grants and
activation/request events, source-wide lifetime quota and one BS-015 acquisition
kind. Original RAW is captured before strict parsing. A deterministic disabled
BS-019A job provides explicit canonical publication/review/recovery. Predictions,
coverage, warm-up, prospective reports and postmatch evaluation use existing ports.
No new prediction model/scheduler/automatic live run or public mutation is introduced.

Additive migration `20261014134905_AuthorizedProviderActivation` adds three tables,
restrictive foreign keys/indexes, trusted clocks, immutable history and deferred
request fencing/rights/quota checks. Existing migration expectations advance 15→16,
ingestion tables 8→11, primary keys 8→11, foreign keys 13→20. One new read-only API
resource advances OpenAPI paths 22→23. Existing artifact formats/kind ordinals remain.

The pooled invoker is a concrete SocketsHttpHandler equivalent to a long-lived
factory-managed client. It disables redirects, proxy, cookies and decompression,
sets connect timeout/pool lifetime and has no secret-bearing logging handlers.
No application HTTP retry exists. Reservations consumed before transport remain
conservative evidence if the process dies; HTTP and DB cannot commit atomically.

## Required scenario coverage

| Scenarios | Controlled automated evidence |
| --- | --- |
| Missing authorization/key/flag/activation; Production; expiry; wrong provider/scope; capability; obligations; revoked rights; synthetic grants/policies cannot activate real HTTP | ProviderActivationWorkflowTests + ProviderActivationGrantTests + existing ApiFootballBoundaryTests |
| 200, 401/403, 429/Retry-After, 5xx, redirects, timeout, cancellation, response size | ProviderActivationWorkflowTests, existing adapter tests; fixed host/path assertions, no real socket |
| Malformed JSON, duplicate IDs, pagination overflow, wrong league/season | Original network RAW retained, no observations, repeat never downloads |
| Concurrent quota, process/scope restart, reactivation | Two contexts race one source ceiling; consumed count is persisted, never refunded |
| Stale owner, disable in-flight, commit-time lease/rights/quota | Application acquisition gates and direct deferred PostgreSQL constraint tests |
| Crash after response / after RAW / before parent acknowledgment | Faulted staging/finalization and replay; original run-linked RAW is recovered, uncertain response blocks |
| Crash after canonical publication | Existing BS-019A real Worker commit-before-ack recovery tests; publication has no HTTP boundary |
| Identity review and approved reprocessing | Network acquisition → ReviewRequired receipt → exact manual bindings → same RAW publication, one HTTP call |
| Rescheduling and result correction | Approved PostmatchResults follow-up grants/jobs, append-only schedule/result correction links and earlier cutoff excludes results |
| Historical observation cutoff and frozen prediction integrity | Existing BS-019A temporal/result/model artifact tests remain; no changed frozen artifact code |
| Prospective eligibility | New acquisition/publication bridge and persisted cohort/monitor tests plus existing coverage/warm-up/frozen-before-kickoff tests; no manufactured predictions |
| Read-only API / PL/EN | Activation GET, no-store, POST405, Production403, revoked403, no request/evidence mutation; rendered views exclude key |
| Legacy compatibility / migrations | Fresh full schema, upgrade from BS-019A retaining RAW/observations/receipts; existing frozen-result/prediction/evaluation preservation migrations |
| Revised prepared child | Recovery blocks rather than replacing an independently revised publication definition; original RAW retained |

All transport fixtures/keys/permission documents in tests are explicitly Synthetic.
No actual provider data, credentials or grant is committed. Operator-owned Web
launch settings remain excluded. See the PR for the exact changed-file manifest,
final head, local totals and CI/CodeQL links.
The [exact changed-file manifest](bs-019b-changed-files.txt) contains 50 files.

## Operational acceptance

Missing: actual operator-specific permission and account/league-season entitlement
evidence, approved current real SourcePolicy, exact real dates/category and quota,
operator credentials, runtime enable/activation and explicit approval to execute
the bounded real canary. Therefore no real RAW/canonical receipt/quota canary can
be verified. Configuration and synthetic tests do not satisfy these gates.

Recommended BS-020: after actual authorized canary evidence exists, collect a
bounded prospective observation cohort and independent coverage/warm-up evidence;
evaluate existing frozen Elo/Poisson/Dixon-Coles predictions honestly. If actual
provider rights impose unsupported retention/attribution, design and approve that
specific lifecycle first. Do not extend providers, algorithms or competitions merely
to evade missing authorization or data.
