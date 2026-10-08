# ADR 0015: Canonical Identities and Temporal Observations

**Status:** Accepted

## Decision

Keep the modular monolith and BS-002 ingestion schema. Domain owns Sport,
Competition, Season, Participant, SportingEvent and EventParticipant. Canonical
UUIDs contain no provider identifiers. Reference sports are football, tennis,
basketball and ice hockey. Team/Individual participants are reusable across
competitions. Two positions support Home/Away or Side1/Side2 roles without
sport-specific columns. Composite foreign keys enforce sport/competition/season
consistency; deletion is RESTRICT.

ProviderIdentity is a separate source-scoped external identifier, unique by
DataSource, entity kind and external ID. IdentityResolution is an append-only
explicit decision ledger: Unresolved, Ambiguous or Resolved, with actor, reason,
decision time, optional RAW provenance and typed canonical target. Each successive
version references its predecessor. A unique identity/version constraint permits
one current decision (highest version), rejecting concurrent competing versions.
Ambiguity has no chosen target. No fuzzy matching or automatic guessing is added.

Observation is an append-only source record with frozen optional canonical target
and provider identity context. A small registry supports DisplayName (text),
ScheduledStart (UTC timestamp) and EventStatus (enum). Values are typed columns,
not arbitrary JSON. Corrections reference an earlier record in the same
identity/type stream, increment version, and cannot backdate availability.
Historical corrections never overwrite the original. Canonical tables remain
current projections and are not historical evidence.

RetrievedAtUtc is acquisition time, AvailableAtUtc is consumer availability,
SourceEventTimeUtc and SourcePublishedAtUtc are optional provenance times.
AvailableAtUtc >= RetrievedAtUtc is mandatory; event/publication time is never
substituted for availability. Application defines the observation query contract.
Infrastructure filters AvailableAtUtc <= AsOfUtc before returning all eligible
history, ordered by availability, creation time, then UUID. It does not dynamically
join the latest identity decision or current canonical projection. Corrections
after the cutoff are excluded. Consumers must explicitly interpret the returned
history; no sport-specific conflict resolver is introduced.

## Consequences

Typed target foreign keys and check constraints prevent dangling/wrong-kind
references. Source/RAW relationships retain provenance. Version/predecessor
constraints prevent cyclic or competing identity histories; correction chains
have strictly increasing versions. Domain validates UTC and shapes; PostgreSQL
enforces temporal and relationship rules. Context guards and database triggers
reject mutation/deletion of identity anchors, decisions and observations, including
EF bulk/raw SQL updates by ordinary application users. Privileged administrators
can bypass triggers: approved licensing/privacy retention remains future work.

New timestamps and cutoffs require PostgreSQL microsecond precision as well as
UTC, rejecting finer .NET ticks instead of rounding predecessor timestamp keys.
No independent current projection updater or correction/conflict winner is added.

Migration is additive and preserves the initial migration and ingestion tables.
Testcontainers tests verify fresh and incremental migration, leakage exclusions,
tie ordering, corrections, constraints and deletion safety. CI/CodeQL remain intact.
No provider calls, ingestion jobs, statistics, features, prediction, authentication
or UI are introduced. SourcePolicy and permitted-use checks are prerequisites
before real ingestion; synthetic test data grants no provider data rights.
