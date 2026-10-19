# BS-019E verification

**ENGINEERING_COMPLETE (correctness verified; performance targets partially met).**

Base: `af468cb`, main after BS-019C / PR #25.
Branch: `perf/bs-019e-query-batching`. Implementation commits: `e674a8e`,
`1df581f`, `ca15ca2`, `cc7b617`, `58d96da`, `71b6838`, `a13ce00`,
`47cfcbb` (audit ADR: `53a4cde`). The tested implementation head is `47cfcbb`;
subsequent verification commits contain documentation/evidence only.

## Bottlenecks and implementation

See [the pre-implementation audit and baseline](bs-019e-baseline.md) and
[ADR 0036](../../adr/0036-bounded-report-prefetch.md).

- Snapshot inspection loads at most 100 artifacts in one query and parses each
  once. Existing manifest validation, stored hash checks, current authorization
  and governance checks run before returning any snapshot batch.
  Source locks use one global sorted order across all manifests, independently
  of the requested output order, to prevent reversed-batch deadlocks.
- Dashboard RAW IDs retain their manifest bindings and individual 20000-ID
  evidence bounds; distinct metadata is read in batches of 1000. Authorization
  groups source/purpose/context without promoting private inspection to display.
  Catalog names are loaded together; a hash set replaces repeated page-key scans.
- Result history is selected with a per-event ordered LATERAL limit of 1001,
  in batches of at most 25 event queries. It preserves per-event 1000-history
  rejection, supersession, source filters and conflict semantics. RAW metadata
  uses batches of 1000; parsing is reused for a RAW/original-scope pair.
- RawReadSession belongs to one dashboard/result read, checks length and SHA-256
  and retains at most 16 MiB / 4096 descriptors. It is constructed explicitly, never
  registered as a service or shared across requests. Current authorization stays
  at callers. No positive authorization is cached. Hashing spans avoids copying
  full payloads just to hash them. Original RAW and recovery are unchanged.
- Quality report classification and denominators are unchanged. Observation
  matching keeps the complete RAW/reference key and ordered zero/one/multiple
  semantics with independent Take(2), in batches of 100 report rows.
- Analytical eligibility batches up to 100 queries, grouping only identical
  cutoff/reconstruction/mode/purpose/context. Observations, RAW, provider anchors,
  historical decisions, supersession, assessments and cross-source conflicts are
  loaded together. Assessment/conflict limits remain per observation, not per
  batch. Source policies are freshly evaluated for historical and current times;
  current policy, operational state and retention are rechecked before the batch
  returns. Missing observations remain ineligible. No concurrent DbContext calls.
- Coverage batches supporting observations, candidate RAW, inventory anchors and
  eligibility. Inventory byte/parse reuse is limited to the read. Existing
  original-scope, temporal, participant, reconstruction and current revocation
  checks remain. Strong inventory validation still has per-record scope/identity
  checks; the partial/unreviewed benchmark does not certify its latency.
- Grouped policy purposes use one fresh history read for the same source/time/
  context; a later invocation always reads again. No persistent cache is used.
- NoBuild verifies SHA-256 of inputs and BetStats output artifacts against a
  successful runner build, rejecting missing/changed/stale outputs. Test calls
  use --no-build/--no-restore. Architecture MSBuild evaluations run at concurrency
  three, retaining every Debug/Release graph and package assertion. Test database
  isolation and disposable class-owned PostgreSQL containers are retained.
  CI builds through BuildOnly to create the freshness stamp before its Full/NoBuild
  step. A regression test exercises this two-stage flow.

## Report measurements

Windows, .NET 10.0.401, Docker Engine 29.6.1 / PostgreSQL 17; synthetic data only.
Setup is outside the query timer. Timing is observational; regression gates use
SQL/RAW counts, not runner latency. Process-wide allocation deltas during Full
can include concurrent tests/background work and are not pure per-query heap cost.
SQL counters cover EF-intercepted reader/scalar/non-query commands; driver-level
transaction control is outside that counter. RAW counts are physical verified
FileSystemRawPayloadStore reads reported by the operation meter.

| Report | Actual records | Before SQL | After SQL | Before RAW | After RAW | Before ms | After ms (Full sample) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Quality | 10 | 222 | 23 | 0 | 0 | 508.33 | 98.50 |
| Quality | 100 | 2202 | 23 | 0 | 0 | 3163.83 | 393.56 |
| Coverage partial | 12 | 284 | 35 | 0 | 0 | 561.63 | 61.33 |
| Coverage partial | 102 | 2264 | 55 | 0 | 0 | 3141.32 | 197.11 |
| Dashboard sparse | 1 | 112 | 66 | 3 | 1 | 162.75 | 133.42 |

Raw samples including post-change allocations and setup times are in
[the measurement directory](bs-019e-measurements/). Quality 100 and Coverage 100
meet their SQL goals on these explicit scenarios. The sparse dashboard result is
one event and must not be represented as a dense 100-event page.

## Paired dense dashboard and stage probes

The same profiler was built against an isolated archive of `af468cb` and the
optimized implementation through `71b6838`. Each run owned a fresh PostgreSQL fixture; 10 and 100
distinct fixtures were asserted. Each fixture has an independent source and
snapshot, so this intentionally measures 100-source governance, rather than a
single-source league season. Original RAW are copied unchanged into one test store.

| Actual fixtures | Before SQL | After SQL | Before RAW | After RAW | Before ms | After ms | Before allocated bytes | After allocated bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 1111 | 588 | 30 | 10 | 1368.14 | 818.80 | 45670824 | 36436896 |
| 100 | 11101 | 5811 | 300 | 100 | 13359.38 | 8014.65 | 444895184 | 349195432 |

For 100 fixtures this is approximately 48% fewer SQL commands, 67% fewer physical
RAW reads, 40% lower latency and 22% fewer allocated bytes. Cumulative pre-query
wall time was 316.60 s before / 263.34 s after; it includes earlier 10-case probes.
The JSON SetupSeconds field is captured after the query and includes it; use the
independent Milliseconds field for latency. Small Fast runs occurred during the
optimized 100-case preparation, before its timed query. These are single local
samples, not statistically controlled production SLOs.
The subsequent global lock-order correction retains the same lock/query count
for this 100-distinct-source workload; its latency was not separately reprofiled.

Separate 100-snapshot EF content materialization including I/O: 132.35 ms /
17002192 bytes before, 140.13 ms / 17003384 bytes after. JSON deserialization:
67.17 ms / 26073712 bytes before, 60.90 ms / 26076216 bytes after. These stage
probes are not additive attribution of the full report; the serializer algorithm
is unchanged. Reuse avoids invoking it repeatedly within the same read.

### PostgreSQL plans and indexes

Committed [raw plans and profiler source](bs-019e-measurements/) record
EXPLAIN (ANALYZE, BUFFERS) on the actual synthetic workload:

- Dashboard key selection: 188.55 ms before / 195.23 ms after. Both scan snapshots,
  expand JSON arrays and use two in-memory 32 kB sorts; the query shape is unchanged.
- One representative scoped result-history LATERAL statement: 0.17 ms after.
- Quality 100-key observation matching: 15.03 ms; bounded assessment history:
  34.77 ms; cross-source conflict metadata: 17.45 ms. The planner used some
  sequential scans (including ExternalId matching and nullable conflict filters).
- Existing identity, observation, RAW, result and assessment indexes were audited.
  No measured candidate index was proven to improve these plans enough to justify
  a migration. Client/evidence round trips dominate the remaining dense dashboard
  latency; no speculative JSONB/expression index was added.

Remaining dense SQL scales with distinct sources and the frozen-evidence/current
authorization and per-result context/identity checks retained inside inspection.
Batching the root fetches does not claim their complete elimination. Therefore
the 100-fixture 300-SQL/2-second goal is **not met** on this workload. Further
changes need dedicated provenance/governance parity evidence, not skipped checks.

To reproduce the paired probe, copy profile-source.cs.txt to Program.cs in an
ignored console project referencing tests/BetStats.IntegrationTests.csproj and
run in Release with Docker. For the before run, reference an isolated git archive
of `af468cb`. The source asserts actual cardinality, writes dense.json and plans,
and excludes setup from its query stopwatch. Test helpers are accessed by reflection
so this remains a diagnostic tool rather than a production API.

## Correctness and operational boundaries

Final Windows Full for implementation `47cfcbb`: **1032/1032 passed**, zero
failed/skipped, **316.48 s** (378 unit + 29 architecture + 625 integration).
Release build passed with zero warnings/errors. Raw TRX and summary are under
`artifacts/tests/Full-e62fa417d910419caef5428306921992`; the committed measurement
directory retains the summary and final report samples.

[CI #77](https://github.com/jodoy/BetStats/actions/runs/39245336741) on source/
evidence head `66c4a0f` passed: **1032/1032**, zero failed/skipped, **259.15 s**
Full runner window; the Release build took 22.54 s and dependency audit passed.
[CodeQL #77](https://github.com/jodoy/BetStats/actions/runs/39245336469) passed on
the same head. That head contains the exact `47cfcbb` implementation; subsequent
commits only finalize documentation and evidence. Latest documentation-head
workflow results are recorded in the PR description.

Intermediate successful Full runs were 1024/1024 (375.66 s), 1030/1030
(332.99 s), and 1031/1031 (382.21 s). They are supporting evidence, not substitutes
for the final run above. All existing 1013 cases plus 19 new cases are retained.

New tests compare the original QualityReports implementation with the new result
on the same data (zero/one/multiple observations, batch boundaries 10/100/500/1000),
batch/single eligibility and result reports, reconstruction and next-read
revocation, RAW corruption/binding/session limits/cancellation, and stale NoBuild
inputs/binaries. Large boundary tests use seeded report rows and a recording gate;
they do not claim a fully imported 1000-event end-to-end benchmark.
The reversed snapshot-batch test forces an overlapping source-lock interleaving
and checks successful results in both original request orders.
As a negative control, the same test was run against an isolated archive of the
pre-fix batch implementation (`a13ce00` plus the new test): it failed as expected
with PostgreSQL `40P01: deadlock detected`. The corrected implementation passed.
That expected mutation failure is not an acceptance-suite failure or a change
to the operator database.

Existing Full covers private/public rights, current revocation, temporal cutoffs,
missing/corrupt RAW, conflicts, concurrency, migrations, least-privilege roles and
backup/recovery. No EF model or schema change, new index, migration or startup
migration was introduced. Real API-Football was never called. Operator credentials
and existing operator database settings were not changed. The pre-existing
untracked Web/Properties directory is not included in the commits.

## Limits and BS-019D

The 500/1000 full-import preparation attempt was stopped after excessive setup
CPU work; it is not a passed or failed acceptance result. Seeded lookup-boundary
tests cover these sizes without representing dense production data. Dashboard
pages remain capped at 100 and larger measurements require explicit pagination.

Fast: 407/407 passed; 11.75 s runner test window after compilation versus 18.21 s
baseline. Verified NoBuild rerun: 407/407, 12.00 s test window / 13.19 s full shell
wall time including fingerprint validation and process startup. The 10-second
goal is not met; MSBuild evaluation and test-host startup remain material.

Full remains above the aspirational 180-second target (316.48 s Windows /
259.15 s Ubuntu CI);
preserved per-class databases, migration/restore drills, container lifecycle and
process overhead are material costs. They were not removed to achieve a timing
claim. Historical BS-019C Ubuntu Full was 288.90 s for 1013 tests; this is context
from a different run, not a controlled paired benchmark. A 1000-event
latency SLO and production-scale performance are not certified.

Recommendation: conditional go for BS-019D engineering preparation
and a small, explicitly bounded first-import canary after correctness gates and
review/merge. Large-volume rollout is not certified by these performance samples.
The actual import retains the existing explicit first-import checklist:
exact scope/window/request bound, reviewed current rights/activation, restricted
runtime identity and a protected verified restore. This change does not authorize
or dispatch a live import, modify data rights or implement an AI Research Agent.

PR: [#26](https://github.com/jodoy/BetStats/pull/26), draft; no automatic merge.
