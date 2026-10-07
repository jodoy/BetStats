# ADR 0013: Project Dependency Direction

**Status:** Accepted

## Context

ADR 0001 establishes the modular monolith. BS-001 makes its existing project
boundaries explicit and executable before application features are introduced.

## Decision

Keep the six existing production projects in one .NET 10 solution. Allowed
project references are:

| Project | Allowed dependencies |
| --- | --- |
| BetStats.Domain | None |
| BetStats.Application | Domain |
| BetStats.Infrastructure | Application, Domain |
| BetStats.Api | Application, Infrastructure, Domain |
| BetStats.Worker | Application, Infrastructure, Domain |
| BetStats.Web | Application, Domain |

API and Worker are composition roots that wire infrastructure to application
ports. Domain and Application remain independent of hosting and persistence.
Web has no direct or transitive dependency on Infrastructure, or direct EF Core
or Npgsql package/assembly references. Infrastructure implements persistence
ports; no database schema is introduced by this decision.

## Consequences

Architecture tests evaluate MSBuild project references in Debug and Release,
check the complete production graph for forbidden edges and cycles, and reject
unregistered production projects and direct assembly references that bypass the
project boundaries. Changes to these rules require an explicit ADR and test
update. Test projects may reference the layers they verify and do not become
production dependencies. No new service boundary is introduced.
