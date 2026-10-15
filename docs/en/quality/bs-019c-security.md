# BS-019C security review

## Audited changes

- ADR 0035 records the initial audit and plan before implementation.
- InspectionAudience.PrivateOperatorInspection is a host-selected reporting
  semantic requiring existing current InternalAnalytics/storage/retention rights.
  It does not imply PublicDisplay; bool report callers preserve their old behavior.
  Frozen UsageContext serialization, historical policy enums and schema are unchanged.
- The Development operator endpoint selects private inspection only after existing
  remote loopback, Host and Origin checks. Its request context is cleared in finally.
  No client audience parameter, public route, RAW body or API key response was added.
- Permission evaluation remains fresh for each read; revocation regressions cover
  both evaluator and HTTP receipt reads. Existing commit fences are unchanged.
- Development backup holds database SHARE locks and uses one exported snapshot,
  preserves referenced originals, validates a sealed bundle and restores only into
  new disposable targets. Operator-enforced offline filesystem/DDL quiescence is
  a necessary condition, not an automatically proven fact.
- Runtime identity is not an object owner, superuser or replication administrator.
  DDL/TRUNCATE/trigger disable/role creation/replication-mode denial and permitted
  ingestion/publication receipt replay are exercised in isolation.
- Credential support retained: Development-only fixed local path, environment
  precedence, bounds, no repository ancestor or file/directory links. The actual
  Windows key file's metadata showed protected inheritance and one owner access
  rule. Its value was not read by BS-019C. Resolver does not promise to validate
  owner-only ACL on every read; operators must preserve the protected file.
- RAW storage additionally rejects worktree `.git` files as repository ancestors.
- Diagnostics accept only bounded code-owned categories, use structured ILogger
  fields and Metrics, and never receive exception objects, RAW or credentials.
- Readiness is Development loopback only, bounded to two seconds, no-store and
  explicitly InfrastructureOnly. It checks DB connectivity and RAW-directory
  readability, not a full manifest scan, write/fsync capability or provider health.
- CI uses locked restore, dependency vulnerability gates, exact action revisions,
  timeout/concurrency controls, Full tests and retained CodeQL.

## Evidence and remaining limits

See verification report for actual test/CI results. Local dependency audit reported
no known vulnerable direct/transitive packages across nine projects. This is not
a penetration test or runtime/container vulnerability certificate. Container and
repository secret scanning remain deployment/repository administration concerns.
No remote/multi-user authentication system was introduced for a loopback tool.

Before real import, apply restricted roles in the operator environment and perform
a representative protected DB+RAW backup drill. The actual existing environment
was not automatically modified. Historical null-length RAW manifests block backup
until an approved compatibility procedure is available. Large verified dashboard
pages and production RTO are not certified by sparse synthetic measurements.

No real API-Football requests were made in BS-019C. A previously authorized manual
HTTP 200 check outside this work is neither durable import evidence nor a grant.
LIVE_CANARY_BLOCKED remains.
