# BS-019C verification

**ENGINEERING_COMPLETE (verified source/test revision) / LIVE_CANARY_BLOCKED**

PR: https://github.com/jodoy/BetStats/pull/25 — no automatic merge.
Base: merged PR #24, `0b434d5e6703ffca0accb5ac7038d2e485d22119`.
Verified runtime/test revision: `2b5640cecdd2a5f9cc9423006ef911c32442cb8d`.
The final documentation-only head and its exact-head workflow reruns are pinned
in the PR description; this document cannot embed its own commit identifier.
No real API-Football request was executed in BS-019C.

## Audit and implementation

[ADR 0035](../../adr/0035-private-inspection-and-operational-safety.md) records the
audit/plan before code. Existing policy evaluation, pipeline/owner/source fences,
RAW store/manifests, publication receipts and PostgreSQL test infrastructure were
extended. No parallel ingestion/recovery system or new prediction algorithm.

PrivateOperatorInspection is a host-selected reporting semantic over current
InternalAnalytics/storage/retention rights; it never implies PublicDisplay.
Development loopback and Host/Origin checks remain; no client audience parameter,
RAW body or key is exposed. Boolean report callers retain their previous semantics.
Historical policy enums and UsageContext serialization are unchanged.

Offline DB+RAW backup uses SHARE locks and one exported PostgreSQL snapshot,
verified originals and a sealed dump/manifest. Restore creates a new disposable
container/directory. Explicit role provisioning transfers ownership to admin,
removes excessive existing/default runtime grants, checks unexpected ownership
and inherited CREATE/TRUNCATE privileges, and grants the reviewed DML/sequence set.
No schema change, startup migration or changes to the existing 16 migrations.

Five suite selections retain all prior cases. ILogger/Metrics diagnostics use safe
categories/IDs/UTC/duration without RAW, headers or exception objects. Readiness
is bounded, local, no-store and InfrastructureOnly. CI restores locked packages,
audits dependencies, pins action revisions, sets timeout/concurrency and retains
Full tests plus CodeQL.

Local credential work was backed up under ignored artifacts and retained. Actual
Windows file metadata was checked without reading its contents: file exists,
no links in its path, protected ACL, matching owner, one owner-only access rule.
Environment precedence, Development-only fallback and malformed/bounded/path
rejection tests remain. Operator-owned Web launch settings and credentials are
excluded from commits.

## Actual final source-revision evidence

- Locked restore: passed.
- Release build: passed, **0 warnings, 0 errors**.
- EF model consistency: `has-pending-model-changes` with Infrastructure as the
  design-time startup project reported **no changes since the last migration**.
- Main-relative and working-tree `git diff --check`: passed.
- Dependency audit: no reported vulnerable direct/transitive packages in nine projects.
- [CI #74](https://github.com/jodoy/BetStats/actions/runs/38731294908): **success**
  on `2b5640c`; independently inspected job logs confirm 374 unit +29 architecture
  +610 PostgreSQL integration = **1013 passed, 0 failed, 0 skipped**, build 0/0,
  dependency gate passed. Full runner wall time **288.90 s**; integration **4m34s**.
- [CodeQL #73](https://github.com/jodoy/BetStats/actions/runs/38731294839): **success**
  on the same revision. Workflow success is not a penetration-test certificate.

All 991 previously published cases are retained; nine credential cases and
13 BS-019C cases bring the total to 1013. Category overlap never duplicates Full.

| Actual run | Environment/revision | Passed / total | Wall seconds |
| --- | --- | ---: | ---: |
| Fast | Windows, final source `2b5640c` | 403 /403 | 15.30 |
| Migration | Windows, final source `2b5640c` | 22 /22 | 81.19 |
| Security | Windows, final source `2b5640c` | 392 /392 | 246.97 |
| Integration | Windows, final source `2b5640c` | 588 /588 | 415.27 |
| Full | CI Ubuntu, final source `2b5640c` | 1013 /1013 | 288.90 |
| Full local | Windows, earlier source `96b1b28` | 1013 /1013 | 446.89 |

Security and Integration were measured concurrently in independent processes with
isolated databases; wall times include shared host load and are not comparative
performance guarantees. Fast + Migration + Integration on the final revision cover
403 +22 +588 =1013 unique cases; Security is an additional overlapping rerun.
The earlier Windows Full preceded the final role-grant hardening; final Full
acceptance is the exact-revision CI run, not a claim about an older binary.

TRX and summaries are retained locally under ignored artifacts/tests; CI uploads
its synthetic test evidence. No real provider payload or credential is an artifact.

## Security, recovery and compatibility gates

- Unit/integration/architecture private/public boundaries passed, including denial
  of public display under private rights, Production/foreign Host/Origin denial,
  no RAW/key response and immediate next-read revocation denial.
- Fresh PostgreSQL 17 and prior-version migration cases passed, including upgrade
  from BS-019A; historical RAW, canonical rows, receipts and frozen data remain.
- Full script backup/restore to a NEW PostgreSQL 17 container passed. Restore waits
  for final TCP readiness, not the temporary initialization socket server.
- Restore verifies original manifest IDs/source/run/key/length/SHA-256 bindings,
  dump/seal integrity and model; corruption/unsealed copies fail closed.
- A nonempty frozen prediction, metadata and result snapshot set is created before
  backup. Its IDs/hashes/content fingerprint remains identical after restore and
  after restricted-role replay/fresh publication.
- Runtime cannot CREATE tables/roles, TRUNCATE, disable RAW triggers or switch
  session replication mode. Re-provisioning removes deliberately injected old
  CREATE/TRUNCATE grants. Legal capture, finalization, job/owner locking, fresh
  canonical publication/receipt creation and original receipt replay still work.
- Existing independent quota concurrency, stale-owner, mid-execution revocation,
  uncertain-attempt/no-refund and original-RAW/no-duplicate-controlled-HTTP tests
  remain green in Full and the additional Security set.
- Runner contract tests exercise empty selection, exit-code propagation and
  exactly three distinct project calls for Full; actual Full confirms the count.
- Two repeatable measurement cases and a separate dense approved 10/100-event
  dashboard measurement completed. [Actual SQL/RAW/time results](bs-019c-performance.md).
  No speculative query/index/authorization cache change was introduced.

## Exploratory failures, corrected before acceptance

An initial pre-fix Windows Full had 34 integration failures (32 missing-logger
fixture failures, one backup fixture lifetime failure and one OpenAPI metadata
failure). Logging DI/fallback, per-case backup database isolation and readiness
metadata were corrected; subsequent complete runs passed. These failed runs are
not counted as acceptance.

A separate earlier Security run had one script-restore failure. Its old test
harness discarded stderr, so the exact original error is not asserted as proven.
The temporary-server readiness hazard was corrected to require TCP, and synthetic
script failure output is now retained by the test. Focused, complete and final
Migration/Security/CI reruns all passed. Final provisioning tests additionally
exposed and fixed retained explicit/default runtime privileges during review.

## Limits and BS-019D

The operator's actual database was not automatically reprovisioned, backed up or
retargeted. Synthetic acceptance does not certify its current runtime identity,
source consent, season entitlement, quota, reviewer/grant, activation or real RAW.
Apply and independently verify those operational steps before the first real import.

Backup consistency requires stopped application/filesystem/DDL writers; the script
cannot certify that assertion. Unreferenced staged files remain in the source and
are not certified by the DB snapshot. Historical null-length RAW manifests block
backup pending an approved compatibility procedure. RPO/RTO depend on a verified
snapshot and representative restore; production/large-data timing is not certified.
Container/secret scanning and future remote authentication remain separate concerns.

Dense approved synthetic dashboard pages were measured (10/100 distinct events),
but production-scale behavior remains unmeasured. Per-row SQL is an explicit
follow-up bottleneck, not a claimed optimization gain.

[Security review](bs-019c-security.md), [test runner](test-runner.md),
[backup/restore EN](../../runbooks/backup-restore.en.md) /
[PL](../../runbooks/backup-restore.pl.md), [roles/upgrade](../../runbooks/postgresql-roles.md),
[BS-019D explicit first-import conditions](../data/bs-019d-first-import-checklist.md).

BS-019D requires separate authorization of exact league/entitled season/category/
seven-day window/request ceiling, current reviewed policy/grant, restricted runtime
identity and a verified protected restore drill. Only then enable/activate and
explicitly dispatch the bounded request. No automatic merge or live execution here.
