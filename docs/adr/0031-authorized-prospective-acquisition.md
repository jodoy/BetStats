# ADR 0031: Authorized acquisition and prospective evidence

Status: Accepted

## Audit before implementation

Fetched main `ea04a2b4c88640d91823752cc91ea1bc3aff406d`; GitHub confirms
BS-016 PR #20 merged. Reviewed ADRs, BS-013–016 code, tests, reports and runbooks.
No BS-017 implementation exists. Reuse AuthorizedProviderExecutor/RequestBudget,
FootballPagination, BS-013 local import plans/ownership and immutable RAW/context,
identity reconciliation, IHistoricalCoverage source-bound kickoff and coverage,
BS-015 durable jobs/leases/fences/recovery, and BS-016 frozen validation reports.
Existing interfaces do not bind provider policy version, acquisition category and
prospective receipt classification into an operator plan/report. No provider
credentials, external authorization, real files or independent live inventory
contract were supplied. Existing owned inventory contracts are synthetic only.

## Decision

Add versioned Application acquisition plans/capabilities, receipts and cohort
contracts. Infrastructure composes existing services using new explicit BS-015
acquisition/cohort job kinds and existing immutable output storage. No new ledger
or schema is needed. CSV acquisition delegates to BS-013. A separate kickoff
acquisition accepts the existing EventTimeSourceClaim file, captures exact RAW
before parsing, then delegates to IHistoricalCoverage.RecordTimeAsync. Pin policy
ID/version and original bytes. Recovery reuses captured RAW and published evidence
under source ownership, never inventing a prior receipt or timestamp.

Extend the source-bound kickoff reader to honor the original explicit historical
CSV profile rather than attempting the legacy metadata parser. Existing profiles,
hashes and historical artifacts retain their original interpretation.

A controlled adapter boundary composes existing executor/budget/pagination,
validates the pinned capability, scope and policy, classifies failures and bounds
responses. It has no HTTP client, endpoint or default transport registration.
Controlled tests supply a fake transport; enabling a concrete real provider needs
a separately reviewed provider-specific implementation and rights/configuration.

Prospective receipts preserve canonical identity, original observation clocks,
source time precision and exclusions. Precise observed-before-cutoff evidence is
necessary but not sufficient for prediction: only the existing approved BS-015
prematch workflow may publish predictions, with its original history/coverage and
warm-up gates. BS-016 links frozen model/evaluation results into a cohort report.
Reads never execute acquisition, prediction or evaluation. Completion describes
inspection, not source completeness or accuracy. Real/operator/synthetic origin,
historical timing and independent-source verification are separate dimensions.
Independent real-source certification remains unavailable without a concrete
supported source contract. Coverage reports never treat parsed rows as inventory.

Use bounded Development-loopback GET projections and PL/EN monitoring. Recheck
current rights, retention and RAW hashes on publication/read. Attribution remains
fail-closed. No automatic startup work, migrations, live provider access, new
prediction algorithms, production deployment or privileged retention bypass.
