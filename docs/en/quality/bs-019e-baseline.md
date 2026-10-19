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

Measurements and PostgreSQL plans are pending. Unmeasured allocation, JSON parse,
EF materialization or repeated-check costs are not claimed as zero.
