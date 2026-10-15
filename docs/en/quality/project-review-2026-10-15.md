# Project review — 2026-10-15

## Scope and evidence

Reviewed the current local main based on merged PR #24 (`0b434d5`), including
uncommitted Development credential-file support. This is a static cross-project
review of host boundaries, dependency registration, persistence/query patterns,
RAW storage, provider execution/recovery, Worker diagnostics, test infrastructure
and CI. It is not an exhaustive line-by-line audit, penetration test or load test.
No provider request was executed for this review. LIVE_CANARY_BLOCKED remains.

Actual checks in this review:
- `dotnet list BetStats.slnx package --vulnerable --include-transitive --format json`
  exited successfully and reported no vulnerable packages across nine projects.
  This does not cover installed runtimes, container images or unknown vulnerabilities.
- `git diff --check` passed (existing LF/CRLF conversion notices only).
- No application changes were made by this review; this report is the only addition.

Earlier evidence, not rerun as part of this review: merged PR #24 had 991 passing
cases; local credential support adds nine cases. The earlier Release build had
zero warnings/errors and the nine credential tests passed. The complete new
1000-case suite has not been rerun. Existing CI/CodeQL success for PR #24 does
not certify the subsequent local changes.

## Findings and priorities

### P1: Private dashboard policy semantics need a decision before real-data display

`Infrastructure/Dashboard/PostgreSqlDashboardQueries.cs:41` explicitly requires
PublicDisplay and passes `PublicDisplay: true`. The loopback-only monitoring
artifact paths also request display authorization through their report services.
The operator supplied permission evidence for private, noncommercial use; it
does not by itself establish public redistribution/display permission.

Do not grant PublicDisplay simply to unblock the UI. Define private operator
inspection separately from public display, document the exact rights boundary,
and regression-test that private permission cannot authorize a public endpoint.
This is currently a fail-closed usability/permission-model issue, not evidence
of an existing public data leak.

### P1: Restore must cover both PostgreSQL and external RAW files

`Ingestion/FileSystemRawPayloadStore.cs` stores original bytes outside PostgreSQL;
the database holds their manifests. The security baseline requires restore-tested
backups, but no executable paired backup/restore procedure was found in the
reviewed runbooks. Database-only restore cannot establish RAW availability.

Before substantial imports, define consistent DB/RAW backup boundaries, protected
storage, recovery-point/recovery-time objectives and a disposable restore drill
that verifies manifests/hashes and resumes publication without provider calls.
Never delete pending files based solely on age: they may belong to recoverable runs.

### P1: Use a restricted database identity for application execution

`docker-compose.yml` bootstraps PostgreSQL using POSTGRES_USER. The official image
creates this bootstrap user with superuser privileges
([official image documentation](https://hub.docker.com/_/postgres)). If the application uses
that same identity, database constraints/triggers are not a security boundary
against compromise of those credentials. Current application credentials were
not inspected, so this is a configuration risk to verify, not a confirmed exposure.

Separate migration/administration and runtime roles, grant only necessary schema
and operation permissions, and test ingestion/recovery with the runtime role.

### P2: Per-row database calls in quality and coverage reports

`Quality/QualityReports.cs:20-35` loops over up to 5000 record groups, querying
observations and calling AnalyticalQualityGate per accepted row. The latter
performs additional provenance, identity and policy queries. Coverage reading
also evaluates each supporting observation and then fetches observations again
(`Coverage/HistoricalCoverage.cs:202-210`). This creates many round trips as
history grows; no latency estimate is claimed without measurements.

Measure query counts, elapsed time and allocations for representative 10/100/5000
record reports. Batch immutable observations/identities/assessments where safe;
preserve AsOf semantics and current revocation checks. Do not share one DbContext
across parallel tasks or cache authorization across requests to mask the issue.
Select indexes using actual EXPLAIN (ANALYZE, BUFFERS) results.

### P2: Dashboard reads perform expensive integrity work

`Dashboard/PostgreSqlDashboardQueries.cs:49-51` reads and hashes RAW files during
queries; later projections verify datasets and read per-event result evidence.
This is defensible for integrity, but repeated navigation can repeat disk and DB
work. Profile before changing it. Prefer an explicit distinction between bounded
status projection and deep verification, with honest verification timestamps and
availability. A summary must never imply an unperformed deep integrity check or
reuse stale authorization.

### P2: Structured operational diagnostics are incomplete

PipelineOperatorCommand emits JSON start/finish events with execution correlation,
but heartbeat failure is caught without a diagnostic event. Several exception
families become generic categories such as database_unavailable or
unexpected_execution_failure. Worker uses Console.WriteLine rather than a common
logging policy; the API uses framework logging without an application-wide
operational event schema.

Add stable event IDs, UTC time, duration, execution/job/source IDs, outcome,
safe error category and bounded retry/lease metadata. Add counters/histograms for
query duration, lease-renewal failure, RAW staging/finalization, quota consumption
and uncertain attempts. Keep command-result JSON separate from diagnostic output.
Configure collection/rotation/retention and levels explicitly. Do not log request
headers, credentials, connection strings, RAW bodies or arbitrary exception text.
No OpenTelemetry/exporter or measured performance instrumentation was found in
the reviewed runtime paths.

### P2: CI reproducibility and supply-chain checks can be tightened

CI and CodeQL restore without `--locked-mode`, despite checked-in lockfiles and
local locked-restore practice. Both workflows reference actions by version tags.
The reviewed workflow set does not include explicit dependency vulnerability,
container or secret scanning jobs. Repository-level GitHub settings were not
audited, so platform-provided scanning may exist separately.

Use locked restore, pin reviewed action revisions and add dependency update/scanning
policy. Configure workflow timeouts, useful failure artifacts and cancellation of
obsolete PR runs. Container scanning must cover the selected PostgreSQL image.

### P2: Readiness and future remote deployment

API health endpoints explicitly report process liveness only. Add a distinct
bounded database/RAW readiness check before depending on automated operation;
do not equate it with provider permission, quota or data/model readiness.

Dashboard has Development + loopback + Host/Origin checks; Web has no-store,
nosniff, frame-ancestors and antiforgery. Authentication/authorization middleware
and rate limiting are not configured. This is not a defect requiring a public
auth system for today's private loopback UI. They become release gates if remote
or multi-user access is introduced. DevelopmentFootballEndpoints only checks the
environment, unlike the dashboard's loopback filter; verify local binding and
apply a consistent boundary before exposing Development on a network.

### P3: Credential-file hardening and maintenance

The local fallback is Development-only, outside Git, bounded, rejects links and
repository ancestors, and yields to the environment key. Its ACL was restricted
when created, but the runtime resolver does not enforce owner-only permissions.
Document that copying/recreating it may change ACLs; consider a safe permission
check and an OS secret store when moving beyond temporary local use. Never move
the key into tracked appsettings. Invalid credential files intentionally fail
closed; a safe reason code would improve diagnostics without exposing content.

Several infrastructure files combine many responsibilities and compressed
statements. Split only when touching a relevant feature, preserving transaction
and lock ordering; avoid a broad formatting/refactor PR before first import.

## Test execution proposal (not implemented)

Keep risk coverage; reduce repeated execution cost. The suite's number alone is
not evidence of redundant cases. It includes valuable concurrency, revocation,
temporal-integrity, immutable-artifact and crash/recovery cases.

Introduce a checked-in runner with a validated parameter:

| Proposed command | Scope |
| --- | --- |
| `./eng/test.ps1 -Suite Fast` (default) | Unit + architecture; local no-Docker tests after classification |
| `./eng/test.ps1 -Suite Integration` | PostgreSQL integration except separately classified migration upgrades |
| `./eng/test.ps1 -Suite Migration` | Fresh database and upgrades from prior schema |
| `./eng/test.ps1 -Suite Security` | Authorization/revocation, quota concurrency, secret boundaries, uncertain requests and RAW recovery |
| `./eng/test.ps1 -Suite Full` | Every test exactly once; no real HTTP |

These commands are a proposal and do not exist yet. Use xUnit Traits such as
`Category=Migration` and `Category=Security` with dotnet test filters, explicit
project selection, nonzero failure propagation and a zero-tests-selected guard.
Security may overlap Integration; Full must select all tests without repeating
overlapping categories. Keep runtime security checks unconditional: a test-suite
parameter must never disable production authorization or validation.

No Traits were found in the reviewed test sources. 31 test classes use
IClassFixture<PostgreSqlFixture>, which creates/migrates a separate container per
class; migration tests also create dedicated containers. This suggests a larger
optimization opportunity than removing assertions. Benchmark sharing a container
with isolated databases/schemas per class while preserving test independence,
parallel safety and dedicated upgrade tests. Do not simply share mutable rows.

Run Fast during normal local work. Before merge, require Full for changes to
schema, provider execution, governance, RAW storage, temporal selection or recovery.
For narrowly scoped changes, use explicit validated test selection while retaining
periodic Full runs. Historical suite reductions should follow per-test timing and
duplicate-scenario analysis; parameterized cases often represent distinct inputs.

## Positive controls observed

- Layered projects and architecture checks; nullable and warnings-as-errors.
- Read queries commonly use AsNoTracking, ordering and explicit bounds.
- Concrete provider host/path, no redirects/proxy, bounded size/time and no
  automatic uncertain-request retry.
- Durable quota and owner/source locking; revocation checked at publication;
  original RAW recovery with receipt reuse.
- RAW hash checks, opaque storage keys, repository/link rejection and atomic rename.
- PostgreSQL bound to loopback in Compose; no automatic live acquisition.
- No sensitive-data logging enablement found in the reviewed host configuration.

## Recommended order

1. Resolve private display semantics and prove a DB+RAW restore with restricted roles.
2. Add suite selection and improve safe operational diagnostics.
3. Measure and batch report queries without weakening authorization/AsOf checks.
4. Tighten CI and deployment readiness, then complete the controlled import gates.

References: [EF efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying),
[HTTP logging and sensitive data](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-logging/?view=aspnetcore-10.0),
[xUnit Traits with dotnet test](https://github.com/dotnet/docs/blob/main/docs/core/tools/dotnet-test-vstest.md).
