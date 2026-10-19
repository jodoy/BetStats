# BS-019E baseline

Base: `af468cb` (main, BS-019C / PR #25). Environment: Windows, .NET
10.0.401, Docker Engine 29.6.1; synthetic data only. Preparation is outside report
timers. Initial sandbox build failed without compiler diagnostics; unrestricted
build succeeded (22.63 s, zero warnings/errors).

Fast after compilation: 403 passed, zero failed/skipped; runner elapsed 18.21 s.
Architecture test time was approximately 10 s, unit test time approximately 1 s;
process startup is included in runner elapsed.

## Audit before production edits

- Dashboard already inspects each distinct snapshot once in FixturesAsync, but
  ChoicesAsync repeats inspection for every choice. Snapshot authorization itself
  performs per-evidence SQL. Fixture result reads repeat per event; RAW display
  authorization repeats metadata lookup, policy reads and physical integrity reads.
- QualityReports performs one EventDate observation query per accepted row;
  eligibility then repeats observation, identity, RAW, assessment and policy reads.
  The observation match is explicitly zero/one/multiple (Take(2)), not First.
- HistoricalCoverage repeats RAW and observation lookups inside candidate/support
  loops. Its existing parsed-fact cache is call-local. Inventory validation has
  additional per-observation original-scope and identity checks.
- AnalyticalQualityGate distinguishes evidence cutoff and reconstruction knowledge
  and separately checks current revocation; grouping cannot erase those checks.
- PostgreSqlDatasets validates immutable manifest structure and hashes plus current
  governance. Replacing InspectAsync with unchecked deserialization is unsafe.
- FootballResults parses a shared RAW for each result and loads context, five
  identity decisions and quality evidence per result; its 1000-history bound is
  per requested scope and must not accidentally become a whole-season batch bound.
- SourcePolicyEvaluator loads policy history per evaluation. History is temporal
  and current revocation must remain observable; persistent positive caches unsafe.
- FileSystemRawPayloadStore reads into one bounded byte array and verifies SHA-256.
  Some callers then copy the entire array solely to hash it again.
- eng/test.ps1 restores/builds once, then starts one test process per project.
  NoBuild does not validate freshness. PostgreSqlFixture owns one disposable
  container per class, preserving data isolation at significant startup cost.

Dashboard page limit is 100. A 500/1000 fixture measurement requires pagination;
raising this limit would change the contract. Existing OperationalPerformanceTests
has explicit actual cardinality and only 10/100 requested cases; its sparse frozen
dashboard case must not be presented as a dense 100-event benchmark.

## Measured original reports

Executed the existing OperationalPerformanceTests against the unmodified main
build before production changes. Authoritative output is retained in
`artifacts/tests/bs019e-baseline/baseline-performance.trx`; the reusable JSON output
paths are overwritten by subsequent runs and are not baseline evidence.

| Report | Requested | Actual | SQL | RAW reads | Milliseconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Quality unreviewed | 10 | 10 | 222 | 0 | 508.33 |
| Quality unreviewed | 100 | 100 | 2202 | 0 | 3163.83 |
| Coverage partial | 10 | 12 | 284 | 0 | 561.63 |
| Coverage partial | 100 | 102 | 2264 | 0 | 3141.32 |
| Dashboard sparse | 10 | 1 | 112 | 3 | 229.96 |
| Dashboard sparse | 100 | 1 | 112 | 3 | 162.75 |

Both measurement tests passed (43 s test duration, including setup). Coverage
actual counts are supporting observations, not the requested CSV row count.
Historical dense-page numbers are available in bs-019c-performance.md, but are
not used as the BS-019E paired baseline. A follow-up isolated archive of original
`af468cb` was built with the same profiler used for the optimized code:

| Actual distinct fixtures | SQL | RAW reads | Query ms | Process allocated bytes | Setup seconds |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 1111 | 30 | 1368.14 | 45670824 | 37.29 |
| 100 | 11101 | 300 | 13359.38 | 444895184 | 316.60 |

Each fixture has an independent synthetic approved source and frozen snapshot;
all original RAW are copied unchanged into the first scenario's store. This
cross-source workload is more authorization-heavy than a single-source season.
The 100-snapshot key query EXPLAIN took 188.55 ms: sequential snapshot scan,
JSON array expansion and two in-memory 32 kB sorts (603 shared buffer hits).
The full read is dominated by the repeated application/evidence round trips.
Setup columns are cumulative pre-query wall time (the 100-case clock also includes
earlier 10-case probes); the raw JSON SetupSeconds field is sampled after the
current query and includes it. Query Milliseconds is timed independently and is
the value used for before/after latency. No setup time is included in that field.
Separate 100-snapshot content materialization including I/O took 132.35 ms /
17002192 allocated bytes; JSON manifest deserialization took 67.17 ms /
26073712 bytes. These stages are separate probes, not additive attribution of
the end-to-end query. No baseline files or business logic were modified.

A first attempt to construct 500/1000 fully imported records exposed very high
setup CPU cost while a transaction was idle waiting for client work. The focused
run was deliberately aborted (67 cases had passed; **not** an acceptance run).
The query-boundary regression now seeds report assessments on valid synthetic
provenance and stubs only the gate, avoiding a large full-import setup. This is
not presented as a full 1000-record end-to-end benchmark.

Original allocation, JSON parse and EF materialization costs were not captured
by the existing benchmark and are not claimed as zero. Follow-up stage probes
must be identified as post-change measurements.
