# BS-019B repository audit

Before implementation: fetched `origin/main` at
`0930dea84663fdfc418044c2f0dd9f7e49265822`; GitHub confirms PR #23
merged on 2026-10-14. Reviewed ADR 0032/0033, API-Football adapter/reader,
publication, BS-015 pipeline ownership/recovery, RAW capture, source policy
evaluation and provider decision matrix. Operator Web launch settings remain excluded.

The existing adapter fixes the HTTPS host, scope, byte bound, status classification
and zero implicit retries. It is unregistered. There is no provider credential
configuration or operator-specific activation evidence. The only registered HTTP
client serves the dashboard. Existing in-memory RequestBudget cannot enforce
provider quotas across workers/restarts. Existing RAW capture is durable and
source-locked; BS-019A already supplies reviewed, immutable publication and recovery.

Smallest safe extension: a source-bound activation grant and append-only activation
events, durable request reservations/outcomes, and one new BS-015 acquisition job
kind. Reuse existing capture and publication jobs; never publish through the legacy
CSV path. No new scheduler or prediction algorithms. Authorization, credential
presence, enablement and observed operational evidence are separate states.

No actual grant, entitlement, credentials or canary approval was supplied.
Operational acceptance is LIVE_CANARY_BLOCKED. Public documentation is technical
evidence only: https://api-sports.io/documentation/football/v3.
