# API-Football publication mapping v1

Implementation: `ApiFootballPublicationMapping`, an unregistered pure mapping boundary.
It does not publish canonical records, approve rights, resolve identity or issue receipts.
Context declares original RAW/source/profile/scope/policy/clocks and is validated against
the exact supplied bytes. The eventual publisher must independently verify those claims
against PostgreSQL and current governance; this mapper is not a security boundary.

The existing BS-018 reader remains authoritative for JSON structure, duplicate members,
duplicate fixture IDs, league/season, pagination, nullable scores and UTC/unix agreement.
Maximum input is 1 MiB, maximum response 500 fixtures, exactly one provider page.

| Provider status | Canonical status | Final result |
| --- | --- | --- |
| NS | Scheduled | None |
| 1H, HT, 2H, ET, BT, P, LIVE | InProgress | None |
| PST | Postponed | None |
| CANC | Cancelled | None |
| FT | Completed | RegulationTime when both final scores exist |
| AET, PEN | Completed | Excluded: unsupported score basis |
| TBD, SUSP, INT, ABD, AWD, WO | No exact mapping | Excluded |
| Unknown | Entire payload rejected by reader | None |

Missing half-time scores remain nullable. They exclude half-time labels without
inventing scores or discarding independently supported full-time scores. A missing or
conflicting kickoff produces an explicit metadata exclusion, never an inferred instant.
Provider status remains in the mapping output. No source publication time is inferred.

Fixture and team references are `provider:api-football:fixture:<id>` and
`provider:api-football:team:<id>`. Every database lookup must additionally constrain
`ProviderIdentity.DataSourceId`. Competition and season use existing ingestion anchor
formats for explicit numeric provider league and season. No names participate in lookup.

Mapping output is a command description, not a second canonical model. Repeated mapping
of the same immutable bytes/context is deterministic. Changed kickoff/score produces new
mapping values for the same event reference. Durable versioning, identity review,
atomic receipts, concurrency and historical as-of behavior belong to the publisher and
are not established by these mapping tests.
