# BS-008.1 verification

Base: BS-008 merged into main as PR #11, commit
`570ed9fa2e3a185940d5a1841e54366d134fa170`.
Branch: `fix/bs-008-1-historical-integrity-swagger`.

## Local commands and results

| Command | Result |
| --- | --- |
| dotnet restore BetStats.slnx | Success |
| dotnet build BetStats.slnx --configuration Release --no-restore | Success, 0 warnings, 0 errors |
| dotnet test BetStats.slnx --configuration Release --no-build | 439 passed, 0 failed, 0 skipped |
| dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure | No pending model changes |

Tests: 186 unit, 13 architecture, 240 integration. The original 382 cases remain
passing, with fixture inputs updated where the corrected contract now requires
bound time evidence or an explicit temporal anchor. Migration count/table checks
include the additional context schema. No tests were disabled or skipped.

All eight audit probes were reproduced on the base before remediation. Their
corrected assertions are maintained in HistoricalIntegrityRulesTests and
HistoricalIntegrityWorkflowTests, alongside additional regression cases.

PostgreSQL 17 Testcontainers verify fresh migrations, append-only original context,
source binding, overlapping approved inventories, RAW availability/hash behavior,
permission-denied no-read behavior and preservation across upgrade. The BS-008
upgrade case exercises original v1 and legacy governance-schema-1 v2 artifact bytes,
hashes and recording timestamps. Published migrations are unchanged. The earlier
BS-007 compatibility fixture removes only later additive schemas in its disposable
database before reapplying migrations; it does not run current ingestion against
an outdated schema.

WebApplicationFactory verifies the actual Swagger UI and OpenAPI JSON routes in
Development, 404 in Production, metadata/schemas/summaries, optional parameters,
400 ProblemDetails, pagination, 405 for mutations to public read routes, and 404
for omitted operator/provider surfaces. A real PostgreSQL case verifies denied
PublicDisplay and that a private canonical sport does not enter the static catalog.

## OpenAPI route inventory

- GET /health/live — preserved liveness contract.
- GET /api/v1/health — typed liveness contract.
- GET /api/v1/sports — bounded project-owned sport vocabulary.

Documentation routes are development-only and are not provider data endpoints.
No database migration/ingestion occurs at API startup. No unrestricted canonical
listing, dataset administration, identity/policy review, RAW bodies, credentials or
restricted provider data are exposed.

## Limits

This milestone supplies contracts and deterministic metadata/evidence processing,
not a model or operational backtest executor. Future evaluation callers must
provide independently validated event evidence. Legacy scope without a matching
complete batch receipt fails closed. Legal RAW removal can preserve artifact
integrity and frozen calculation while preventing deep provenance inspection.
Privileged database owners can bypass SQL history guards; deployment roles remain
an operator requirement.

CI and CodeQL outcomes must be evaluated on the published PR head; local success
alone is not a GitHub check result. No automatic merge is authorized.
