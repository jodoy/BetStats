# BS-017 verification

## Baseline, audit and implementation

Fetched main `ea04a2b4c88640d91823752cc91ea1bc3aff406d`; PR #20 confirmed merged.
ADR 0031 records the pre-implementation audit and security decisions. Existing
BS-013 import/RAW/reconciliation, BS-014 read restrictions, BS-015 jobs/ownership
and BS-016 validation remain authoritative. No new schema, package or prediction
algorithm. Existing enum values and legacy page-request bytes are preserved.

Application adds versioned capability/plan/page context, receipt, status and cohort
contracts. Infrastructure composes existing services for explicit local CSV and
source-bound kickoff acquisition, immutable reports and governed controlled pages.
Worker extends explicit Development actions; API adds three bounded loopback GETs.
Web supplies PL/EN monitoring. Historical kickoff binding now selects the original
explicit CSV profile; it does not reinterpret or rewrite legacy RAW/artifacts.

No real files, provider authorization, credentials or independently supported real
inventory contract were supplied. Tests use project-authored synthetic data and
controlled in-process transport only. No live provider, scraping, deployment,
training or operational accuracy validation occurred. Operator origin is a claim;
independent source verification/real completeness remain unavailable.

## Verification status

- Locked restore: passed locally.
- Release build: passed locally, zero warnings/errors.
- Complete local solution run: **787 passed, 0 failed, 0 skipped**: 350 unit,
  23 architecture and 414 disposable PostgreSQL integration tests. Local integration
  took 6 minutes; TRX counters independently confirm all three totals. New cases:
  21 unit + 1 architecture + 29 integration/controlled-adapter tests.
- EF `has-pending-model-changes`: passed; no pending model changes/migrations added.
- `git diff --check`: passed. Documentation links resolve.
- Implementation SHA `24844fd62eb267b53a3b714dc01924d34fcee4a6`:
  [CI #58](https://github.com/jodoy/BetStats/actions/runs/38422895236) passed.
  Logs confirm all 787 tests, zero failed/skipped and zero build warnings/errors;
  GitHub PostgreSQL integration duration was 4m17s.
- [CodeQL #57](https://github.com/jodoy/BetStats/actions/runs/38422895035) passed
  for that SHA. Final documentation-head CI/CodeQL links are maintained in PR #21.

## Coverage and evidence

New tests cover authorized capture, unknown publication, exact RAW preservation,
duplicate/idempotent dispatch, wrong/malformed kickoff, unknown precision,
source/policy revocation, policy pinning, scope/category/version limits, independent
kickoff and observation clocks, future evidence rejection, controlled 429/cooldown,
timeouts, budget exhaustion, bounded/cyclic pagination, policy revocation during
response, postmatch corrections, original-profile kickoff binding, recovery and
live-source/execution ownership, corruption, retention/attribution restrictions,
unchanged legacy predictions/ledgers, Development-only GET/no mutation and PL/EN.
The cohort links existing pipeline-frozen predictions without counting unwarmed
priors as eligible and preserves original BS-016 evaluation reports. Manual strong
completeness is rejected by existing review; partial coverage remains partial.

Existing suites cover concurrent fencing, cancellation, attempt budgets, sorted
source governance, historical leakage, result/evaluation eligibility, calibration,
all three existing models, immutable hashes and fresh/upgrade migrations. The
complete suite passed locally and in GitHub CI for the implementation SHA above.

## Limits

Local acquisition: <=1 MiB original bytes, <=500 CSV rows without truncation.
Cohorts: <=10 acquisitions, <=3 validation executions and <=2 coverage queries.
Status: <=100 sanitized execution heads. A blocked parent can retain permitted
RAW/child observations already committed; there is no destructive rollback of
audit history. Provider page context pins source,
policy/version, scope/category; per-page existing budgets and timeout apply, with
bounded pages/cursors and aggregate response size. No transport is registered;
there is no runtime configuration switch that enables a real API. Existing quotas
and cooldown are per-process and reset on restart; they are not a distributed or
durable provider quota. A concrete provider integration must address its actual
quota contract rather than relying on that boundary as certification.

Acquisition completion is not completeness, prediction eligibility or accuracy.
Reports are frozen snapshots; later corrections require a new explicit cohort.
Metadata/result publication, availability, recording and kickoff evidence remain
separate. Unknown timestamps stay null; no historical receipt is fabricated.
Prospective recording is necessary but original BS-015 history/warm-up gates are
still required. Existing owned coverage contracts certify synthetic inventories
only. Real-source declarations remain Unknown without an independent contract.
Exports/backups require approved retention; no purge bypass is introduced.

Recommended BS-018: one reviewed provider-specific authorization/schema/transport
contract, independent inventory/as-of proof and a genuinely observed prospective
cohort before any empirical calibration or accuracy claim.
