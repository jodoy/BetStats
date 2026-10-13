# BS-018 verification — authorized-access fallback

Baseline: `origin/main` `ae49fcecdce3e320e02b11e6d9688e3151bfae21`,
confirmed merged BS-017 PR #21. Branch `feat/bs-018-first-authorized-provider`.
Audit and pre-implementation decision: [ADR 0032](../../adr/0032-first-authorized-provider.md).

## Delivered scope and actual status

No provider was approved for live use and no credentials or real grant were supplied.
The explicit missing-authorization fallback in BS-018 section 3 applies.
API-Football v3 is the **technical boundary candidate**, not an authorized live source.
All new executable provider evidence is fictional/mocked. No live provider request,
real file download, subscription, grant, canonical observation, prospective prediction
or real result/evaluation was created. Live operational acceptance remains blocked.

Delivered: one documented endpoint, fixed seven-day league/season scope, original
JSON content, versioned strict reader, policy-pinned authorization checks, existing
executor/budget/timeout/cooldown, no implicit retries, PL/EN blocked-readiness page,
decision matrix and configuration/canary/recovery runbooks. No runtime transport
registration or HTTP factory exists. Existing pipeline/ingestion/model/evaluation
implementations and database schema are unchanged. The CSV parser deliberately
does not interpret this JSON profile; tests prove original RAW survives that denial.

Pending authorization and follow-up approved implementation: persistent JSON-to-
canonical publication, linked kickoff/result corrections, provider-specific durable
recovery, real quota reconciliation, live opt-in dispatch and actual prospective
canary. No fake commands, receipts, remaining quota, last-success dates, independent
completeness or accuracy are presented. The static readiness page describes this
release only; the existing BS-017 monitor describes actual stored reports.

## Commands

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx -c Release --no-restore
dotnet test BetStats.slnx -c Release --no-build --logger 'trx;LogFilePrefix=bs018-full' --results-directory .artifacts/bs018-full
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
git diff --check
```

Integration databases are disposable PostgreSQL 17 Alpine Testcontainers, never
the operator's running database. Existing untracked Web launch settings are excluded.
No migration or package changes. Final local and exact-head CI results are recorded
in the pull request after verification finishes.

## Test coverage and limits

| Requested behavior | Evidence in this fallback |
| --- | --- |
| Authorized configuration; missing/bad credentials; unsupported capabilities/scope | New mocked boundary/configuration tests; 401/403 are mocked, never validation of a real key |
| Revocation, pinned policy version, disable while response is in flight | Mocked authorization and switch tests; current DB policy evaluated in RAW test using the existing synthetic source |
| Fixture/kickoff mapping, precision/timezone, duplicate/conflicting fixtures | Strict JSON reader tests; DTOs remain provider observations, not reviewed canonical identities |
| Postponed/cancelled/unknown statuses and missing/corrected results | Status and correction tests; no canonical correction publication is claimed |
| HTTP 429 / both Retry-After forms / timeout / errors / size / malformed JSON | Mocked transport and shared executor tests; sanitized errors and zero implicit retries |
| Pagination and request budget/concurrency | Single-page fixtures contract enforced; existing budget denies concurrent/exhausted calls |
| RAW before parsing | New PostgreSQL tests preserve exact JSON/hash/profile/scope/database clock and audit while rejecting unsupported publication; prior source observations/results unchanged |
| Interrupted acquisition, leases, idempotence, schedule cancel/disable | Existing BS-015/017 PostgreSQL regression suite remains authoritative for local operations; live JSON recovery/dispatch is pending and **not** certified by those tests |
| Leakage, warm-up, frozen predictions and evaluation eligibility | Existing complete regression suite; no provider DTO enters a model, no new predictions or gates |
| Dashboard GET/read-only and no startup HTTP | Architecture test forbids runtime registration/dispatch, page has no injection/execution; existing HTTP boundary tests retained; new HtmlRenderer verifies PL/EN |
| Legacy immutable compatibility | Existing full RAW/prediction/evaluation/hash/clock/operation-ledger regressions; no old profile or artifact rewritten |

## Security review

Credential is private and absent from descriptors, public DTOs and diagnostics.
Fixed HTTPS origin/path and numeric/date query values; no arbitrary URL or cursor.
Only an operator-supplied key is accepted. Configuration/current policies checked
before sending and again before returning content. Existing source operational
status/executor budget surrounds the boundary. The real handler (not delivered)
must disable redirects, retries, cookies and secret/body logging. Stream reading
is bounded to 1 MiB, JSON nesting to 32 and rows to 500; duplicate members rejected.
No source approval or credential is seeded. Retention/attribution restrictions remain
fail-closed; no implied purge plan. No startup work/migrations, public mutations,
scraping, deployment or provider data committed.

## Recommended BS-019

Obtain one operator-reviewed provider grant, exact league/season entitlement and
credentials, then implement and verify the missing durable JSON publication and
safe opt-in canary using the existing BS-015 child/fencing contracts. Establish
independent coverage/as-known history and record real observations before kickoff
before making any real performance claim. This is required work, not an assertion
that the technical mock boundary has completed live BS-018 acceptance.
