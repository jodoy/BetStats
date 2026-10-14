# BS-019B verification

Engineering acceptance is tracked by the final PR's exact-head CI/CodeQL and local
verification evidence; the PR remains Draft until all engineering gates pass.
Operational status: **LIVE_CANARY_BLOCKED**. No real API-Football requests were made.

The complete suite comprises **991 cases**: 372 unit, 28 architecture and 591
disposable PostgreSQL integration cases. This adds 62 cases to the previous 929
(14 unit, 2 architecture, 46 integration). Required commands: locked restore,
Release build without restore, full solution test without build, EF pending-model
check and `git diff --check`. The PR records actual passing totals, warnings/errors
and workflow links rather than treating the case count as a passing result.

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
