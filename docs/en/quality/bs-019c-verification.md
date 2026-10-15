# BS-019C verification

Status: verification in progress; not ENGINEERING_COMPLETE.
LIVE_CANARY_BLOCKED. No real API-Football request and no automatic merge.

Base: main with merged PR #24, `0b434d5e6703ffca0accb5ac7038d2e485d22119`.
Branch: feat/bs-019c-data-safety-readiness.

## Audit and implementation

[ADR 0035](../../adr/0035-private-inspection-and-operational-safety.md) records the
audit/plan before code. Existing policy evaluation, pipeline/owner/source fences,
RAW store/manifests, publication receipts and PostgreSQL test infrastructure are
extended, not replaced. Local credential-file work was backed up under ignored
artifacts before changes and retained. Operator Web launch settings and actual
credentials are excluded from commits. The actual key was not read in BS-019C.

No EF schema/enum/UsageContext serialization changes or startup migrations.
Existing 16 migrations remain; private inspection maps to existing internal rights
and never to PublicDisplay. Role ownership/grants are explicit administration.

## Evidence completed before full acceptance

- Locked restore and Release compilation: passed, zero warnings/errors.
- Dependency audit (direct + transitive, nine projects): no reported vulnerabilities.
- Focused private HTTP/DB+RAW/restricted-role tests: 3/3 passed.
- Runner negative/selection and role/capture tests: 5/5 passed after corrections.
- End-to-end Development backup/restore scripts into a NEW PostgreSQL 17 container:
  1/1 passed, approximately 21 seconds. No operator DB was restored or altered.
- Two independent synthetic performance measurement cases passed, about 38 seconds.
  Actual counts/times/limits: [performance report](bs-019c-performance.md).
- Windows credential-file metadata: inheritance protected, one owner access rule;
  no value included. Existing environment-key precedence tests retained.

Initial Fast run identified an architecture assertion still expecting the old
boolean HTTP audience. It was updated to the new explicit private audience; do not
count that failed exploratory run as acceptance. The runner's own negative-test
harness was corrected to propagate its child exit code and isolate mocked calls.

## Final acceptance evidence

Pending: final Full and all suite timings, EF model consistency, final diff check,
exact source/test revision, final-head CI and CodeQL, PR URL. These must be filled
from actual completed results before declaring ENGINEERING_COMPLETE.

## Documentation

- [Security review](bs-019c-security.md)
- [Performance measurements and limits](bs-019c-performance.md)
- [Test runner](test-runner.md)
- [Backup/restore EN](../../runbooks/backup-restore.en.md) /
  [PL](../../runbooks/backup-restore.pl.md)
- [Runtime/admin roles and upgrade](../../runbooks/postgresql-roles.md)
- [BS-019D explicit entry conditions](../data/bs-019d-first-import-checklist.md)

Engineering tests do not certify that the operator's current database has already
been provisioned with restricted roles, backed up or authorized for real import.
The operator must apply/verify those steps before BS-019D. Large approved dashboard
pages and production restore time remain unmeasured. Full test coverage is retained.
