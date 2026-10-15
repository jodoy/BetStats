# ADR 0035: Private operator inspection and operational safety

Status: Accepted for BS-019C implementation, 2026-10-15.

## Audit and decision

Reuse SourcePolicyEvaluator, immutable RAW/manifests, PostgreSqlPipeline fencing,
publication receipts, FileSystemRawPayloadStore and existing migration tests.
Local credential-file changes are retained; operator Web launch settings and
actual credentials are excluded. The baseline is merged PR #24 on main.

PrivateOperatorInspection is an explicit reporting audience, not PublicDisplay.
It requires current InternalAnalytics, storage and retention rights and is only
selected by the Development loopback Host/Origin-checked operator endpoint.
Legacy boolean report callers retain their existing public/internal behavior.
Existing policies and stored enums are unchanged; no historical rights are granted.
Public endpoints must never select the private audience from a client parameter.

Development backup uses an offline, operator-enforced quiescence boundary plus
database access exclusion during pg_dump and RAW copying. A backup is only sealed
after manifest/file validation. Restore targets a new disposable PostgreSQL 17
container and a new RAW directory, never an existing application database.
Database ownership stays with administration; runtime receives DML/sequence/use
privileges only, with no DDL, ownership or trigger-disabling privileges.

Test suites are explicit selections over retained tests, with TRX-based zero-test
and failure checks. Full selects each project once. Diagnostics use safe fixed
categories with IDs, UTC and duration, never exceptions/RAW/secrets. Readiness
checks infrastructure only and cannot authorize a provider request.

## Implementation sequence and acceptance

1. Preserve local work; implement private audience and boundary regressions.
2. Implement DB/RAW backup+restore, restricted roles and recovery drills.
3. Add suite classification/runner, diagnostics and infrastructure readiness.
4. Harden CI, measure quality/coverage/dashboard without speculative optimization.
5. Locked restore, Release build, full PostgreSQL 17 tests, suite timings,
   model consistency, final-head CI/CodeQL and an unmerged PR with actual evidence.

No live API requests. LIVE_CANARY_BLOCKED persists. Missing acceptance evidence
must be reported as BLOCKED, not replaced by a claim of readiness.
