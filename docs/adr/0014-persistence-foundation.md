# ADR 0014: Persistence Foundation and RAW Metadata

**Status:** Accepted

## Context

BS-002 adds the first PostgreSQL schema to the existing modular monolith.
ADR 0003 selects PostgreSQL; ADRs 0004/0005 require provider separation and
permitted RAW capture before normalization. ADR 0013 defines project boundaries.

## Decision

Infrastructure owns EF Core 10, the Npgsql provider, BetStatsDbContext, entity
mappings, design-time factory and committed migrations. API and Worker call
Infrastructure's DI registration; Domain, Application and Web do not reference
persistence packages. ConnectionStrings:BetStats is the configuration key.
Registration does not open a connection. Missing configuration is reported when
a context is requested, rather than preventing the existing liveness host from
starting. Migrations are applied manually, never on host startup.

The initial schema contains only infrastructure metadata:

- DataSource identifies source configuration by a unique required code. It
  contains no provider credentials and is not a future canonical sports entity.
- IngestionRun records execution status/times and a non-sensitive error code.
- RawPayload records immutable capture metadata, provenance, a SHA-256 digest
  and an opaque storage reference. It contains no payload bytes or credentials.

All keys are UUIDs. Timestamps are UTC DateTime values mapped to PostgreSQL
timestamp with time zone; callers supply them, with microsecond precision.
Required text is nonblank. Status is stored as a constrained string. Run times
cannot end before they start. Raw capture/run links must belong to the same source;
a composite foreign key enforces this even outside EF. Every relationship uses
RESTRICT deletion, preventing accidental cascade loss of historical metadata.
RawPayload is append-only through this DbContext: EF updates/deletes are rejected.
No global hash uniqueness is imposed: the same content may be captured repeatedly
at different retrieval times. Source/time indexes support provenance queries.

The design-time factory uses the standard environment configuration provider.
Offline migration generation/scripts can construct a provider model without a
connection string. Applying migrations requires explicit connection configuration.
The local EF CLI version is recorded in a tool manifest.

## Consequences

Real PostgreSQL Testcontainers tests migrate a fresh disposable instance and
verify constraints and round trips. They never use a developer or production
connection string. Missing Docker fails tests instead of silently skipping them.
CI uses a Linux runner with Docker and retains unit/architecture/CodeQL gates.

SourcePolicy and purpose-specific licensing checks are prerequisites for any
future ingestion. Configuring a source does not grant display, training,
redistribution or commercial rights. ExternalReference is provider metadata;
it is never a canonical domain identifier. RetrievedAtUtc and CreatedAtUtc retain
capture provenance; future Observation/Canonical and AsOfUtc processing remains
out of scope, as do prediction snapshots and object storage infrastructure.

Immutability does not override licensing/privacy retention obligations. A future
approved retention process must explicitly audit deletion of payload storage and
dependent metadata; raw SQL/database administrators can bypass the EF guard.
No retention job, immutable-storage claim, or ingestion scheduler is introduced.
