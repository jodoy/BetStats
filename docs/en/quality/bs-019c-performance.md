# BS-019C performance measurements

Initial local Windows/.NET 10/PostgreSQL 17 disposable-container measurements,
Release, synthetic fixtures only, 2026-10-15. Measured wall time includes async
database round trips; no load/SLO claim. Setup/import/model freezing is excluded.
DbCommandInterceptor counts commands for the actual report context; a scoped
Metrics listener counts verified RAW reads, without collecting content or IDs.

| Path | Requested size | Actual records | SQL commands | RAW reads | Time ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Quality, unreviewed | 10 | 10 | 222 | 0 | 439.25 |
| Quality, unreviewed | 100 | 100 | 2202 | 0 | 2529.23 |
| Coverage, unreviewed partial inventory | 10 | 12 | 284 | 0 | 444.17 |
| Coverage, unreviewed partial inventory | 100 | 102 | 2264 | 0 | 2368.84 |
| Dashboard, sparse frozen page | limit 10 | 1 | 112 | 3 | 206.51 |
| Dashboard, sparse frozen page | limit 100 | 1 | 112 | 3 | 173.40 |

Coverage includes two pre-existing supporting observations. Unreviewed fixtures
exercise real fail-closed eligibility checks, not approved complete inventory.
Dashboard limits do NOT mean 10/100 returned fixtures: the frozen dataset has one
candidate. The larger dashboard data-volume case remains an explicit measurement
limit; these samples do not certify dense pages or 5000-row reports.

## Dense approved dashboard follow-up

A separate ignored local measurement harness reused ResultOperationsWorkflowTests
and BacktestWorkflowTests.Definition in a fresh PostgreSQL 17 container. It built
10 then 100 independent synthetic approved/frozen fixture scenarios, retained all
source lifetimes and copied their original RAW into one shared test store without
changing keys/hashes. It queried an unfiltered dashboard page and asserted actual
cardinality before reporting the sample. Setup is excluded; other local tests
were running, so wall time reflects shared host load. No production code changed.

| Actual returned fixtures | SQL commands | Verified RAW reads | Time ms |
| ---: | ---: | ---: | ---: |
| 10 | 1111 | 30 | 1748.37 |
| 100 | 11101 | 300 | 17852.73 |

Unlike the sparse measurement, these pages really returned 10/100 distinct events.
The approximately 111 commands and three RAW reads per fixture reinforce the
per-row verification bottleneck. This is a cross-source synthetic workload, not
a production league-history latency guarantee. The local harness and raw JSON
are retained under ignored artifacts/bs019c-dense; the PR records results and
fixture construction, not an additional runtime/ingestion implementation.

## Findings

Quality uses approximately 22 commands per record. Coverage repeats similar
per-observation identity/policy/quality work. The sparse dashboard already uses
112 commands and three integrity reads for one row; pagination alone does not
address per-row verification cost. Latency differences at identical dashboard
cardinality are ordinary small-sample variation, not an optimization gain.

No speculative query/index/cache change was made in BS-019C. Next optimization
should introduce a bounded batch eligibility/read operation over immutable IDs,
measure a dense approved dataset and compare command count and latency. Preserve
AsOf cutoffs, identity decisions, hashes and fresh current-policy revocation checks.
Do not run concurrent work on one DbContext or cache authorization across requests.
Choose indexes only after representative EXPLAIN (ANALYZE, BUFFERS).

The two measurement cases are repeatable in Full/Integration tests; current raw
samples are written to ignored artifacts/performance and captured in test output.
Later suite runs may have different times because of container/host load. The
table is the initial independently run measurement, not overwritten claims.
