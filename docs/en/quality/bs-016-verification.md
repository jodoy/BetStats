# BS-016 verification

## Baseline and scope

Base main `c99e2e8c2c32c267bdfa0f4b4edd5a7593dabd3e`, confirmed merged
BS-015 PR #19. Audit and decisions are recorded in ADR 0030 before implementation.
No authorized real dataset, provider credentials or permissions were supplied.
All new executable dataset scenarios are explicitly synthetic software tests.
Real-data readiness, provider compatibility, operational accuracy and empirical
calibration are **unavailable**, not passed. Operator origin classification remains
a claim. No provider calls, scraping, training or production deployment occurred.

Application adds bounded versioned validation contracts and a read-only report
port. Infrastructure binds approved RAW/profile/scope, checks current rights and
retention, reuses quality gates and verifies frozen BS-012/011 publications.
Worker extends explicit BS-015 commands; pipeline receipts/leases/cancellation and
recovery remain authoritative. API adds one Development-loopback GET projection.
Web consumes Application DTOs in a bilingual report view. No new package, database
schema/migration or change to an existing immutable manifest contract is required.

## Verification status

- Locked restore: passed locally.
- Release build: passed locally, zero warnings/errors.
- EF `has-pending-model-changes`: passed locally; no pending model changes.
- New unit tests: 20 passed initially; final full suite results recorded below.
- Initial PostgreSQL attempt: failed to initialize fixture because Docker was
  unavailable. Docker Desktop startup itself crashes on its `dockerInference`
  socket. This is an environment failure, not a passing integration run; no tests
  were weakened or marked skipped. Final CI results will be recorded below.
- Final CI/CodeQL: pending.

## Coverage map

New `FootballValidationTests` cover approved synthetic RAW, profile/scope/hash
binding, origin labeling, unsupported inputs, missing results, uncertified kickoff,
missing evaluation, revoked/missing permissions, RAW corruption, report retention,
read-only HTTP, Production denial, bilingual rendering, idempotent dispatch,
live-owner exclusion/expired recovery, all three existing model publications,
malformed/conflicting CSV and preservation of legacy artifact bytes/ledgers.
`FootballValidationBoundaryTests` checks the GET/read-only port and explicit Worker
boundary. Existing architecture graph tests enforce Infrastructure ownership.

Unchanged BS-011–015 suites continue to cover chronological/cutoff leakage,
future corrections/reviews, independent result eligibility, numerical model
distributions, warm-up, null calibration, immutable prediction publication,
fenced concurrent owners, cancellation/retry limits and fresh/upgrade migrations.
The full-suite result must pass before these are reported as verified for BS-016.

## Reproducibility and limits

Reports freeze their input hash, exact RAW hash and scope, result observation
identifiers/times, original prediction hashes and optional evaluation cohorts.
Repeated reads preserve bytes and never run predictors. Explicit readiness
inspection may deeply replay existing models without creating a new publication.
Verification checks report/definition hashes, current rights and original RAW;
it does not reinterpret a frozen report using later corrections.

Bounds: one <=1 MiB RAW, <=500 rows, <=2000 parse issues, <=3 prediction pairs;
linked manifest/RAW bounds remain enforced. Exceeding a bound rejects the request.
CSV dates and optional Time columns do not certify source-bound UTC kickoff.
Result availability, source publication and database recording remain distinct.
Imported historical observations are never backdated. Missing pre-event evidence
cannot be repaired by calling the pipeline with a past planned time.

`Completed` means the validation operation finished; its report can be Incomplete,
Blocked or Unavailable. Verified denotes scoped software/evidence checks only,
never source-wide completeness, model calibration or operational accuracy.
Synthetic fixtures retain their label even when software checks pass. Recovery
testing does not prove recovery behavior on an operator-supplied real dataset.
Current authorization governs every read/publication; attribution restrictions
fail closed. Exported report retention remains an operator obligation.

Recommended BS-017: approved real-data acquisition/archival and independent
as-of/coverage evidence, followed by a prospective frozen evaluation cohort.
