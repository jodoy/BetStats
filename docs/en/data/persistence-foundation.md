# PostgreSQL Persistence Foundation

BS-002 adds provider-neutral ingestion metadata, not sports-domain entities.
EF Core 10 and the Npgsql provider belong to Infrastructure. API and Worker call
`AddPersistence`; registration and context resolution do not connect to PostgreSQL.
There is no startup migration, `EnsureCreated`, ingestion job or generic repository.
See [ADR 0014](../../adr/0014-persistence-foundation.md).

This page documents the BS-002 ingestion layer. BS-003 adds independent canonical
and provenance schemas; see the [canonical model and historical query contract](canonical-sports-model.md).
The RAW guard limitations below still apply to ingestion metadata; the new
provenance tables additionally have database immutability triggers.
BS-004 adds [source policies, audit and trusted identity availability](source-governance.md)
in an additive migration; existing ingestion definitions remain intact.

## Schema and provenance

Tables are in the `ingestion` schema; EF migration history is in `public`.

| Table | Meaning | Constraints and indexes |
| --- | --- | --- |
| DataSources | Source configuration: UUID Id, Code, DisplayName, IsEnabled, CreatedAtUtc | Unique case-sensitive nonblank Code (100 chars); nonblank DisplayName (200 chars) |
| IngestionRuns | Execution: UUID Id, DataSourceId, Status, optional StartedAtUtc/CompletedAtUtc/ErrorCode, CreatedAtUtc | Source FK; Pending/Running/Succeeded/Failed string status; completion must follow a nonnull start; source/created-time index |
| RawPayloads | Immutable capture metadata: UUID Id, DataSourceId, optional IngestionRunId/ExternalReference, RetrievedAtUtc, ContentHashSha256, ContentType, StorageKey, CreatedAtUtc | Source FK; optional composite run/source FK; lowercase 64-character hex SHA-256; nonblank content type/storage key; source/retrieval, run/source and hash indexes |

All FKs use RESTRICT. The composite FK prevents a capture from referencing a run
for another source. Repeated hashes are allowed to preserve separate retrievals.
UUIDs and UTC timestamps are supplied by callers. PostgreSQL stores timestamps
as `timestamp with time zone`, at microsecond precision; non-UTC DateTime values
are rejected by the context. `RawPayload` has init-only fields and context saves
reject updates/deletes. Raw SQL and EF bulk updates can bypass that guard; this
is not a database-wide immutable-storage guarantee.

`ExternalReference` describes the provider capture, not a canonical sports ID.
`StorageKey` is an opaque reference, not a signed URL or embedded payload. No
payload bytes, API keys, authorization headers or free-form error traces are
stored. `ErrorCode` is a non-sensitive classification, not a provider response.

Before future ingestion, each source requires SourcePolicy and explicit permitted
purposes. Source configuration does not grant storage, display, training,
redistribution or commercial rights. RAW -> Observation -> Canonical, canonical
sports identities, temporal AsOfUtc features and immutable prediction snapshots
remain future work; this metadata preserves the provenance needed for it.

## Development PostgreSQL

Docker must run Linux containers. From the repository root:

```sh
cp .env.example .env
# Edit ignored .env: set a local password and matching connection string.
docker compose up -d --wait postgres
docker compose ps
docker compose exec postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
docker compose logs --tail 50 postgres
docker compose down
```

PowerShell can use `Copy-Item .env.example .env`. Compose reads `.env` for
`POSTGRES_DB`, `POSTGRES_USER`, required `POSTGRES_PASSWORD` and optional
`POSTGRES_PORT` (default 5432). The port binds only to 127.0.0.1. Data survives
shutdown in the named `betstats-postgres` volume. Readiness checks database
availability, not application schema currency. Changing POSTGRES credentials
does not alter users/passwords in an already initialized volume.

.NET does not read `.env` automatically. Set `ConnectionStrings__BetStats` in
the host/EF CLI process environment, with the same database/user/password/port.
Use standard configuration providers, not hardcoded credentials. The EF factory
uses environment configuration; it does not load application JSON or `.env`.
Without a connection string it supports offline model/script generation only.

```powershell
$env:ConnectionStrings__BetStats = Read-Host 'Local PostgreSQL connection string' -MaskInput
```

In Bash, `read -r -s -p 'Local PostgreSQL connection string: ' ConnectionStrings__BetStats`
then `export ConnectionStrings__BetStats` avoids putting the value in shell history.
API `/health/live` and Worker can start without database configuration; requesting
a DbContext requires the connection string. Startup never probes the database.

## Migrations

Use the committed .NET tool manifest and run from the root:

```sh
dotnet tool restore
dotnet ef migrations list --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations script --idempotent --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure --output artifacts/persistence.sql
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
```

Review the SQL before applying it. `database update` requires explicit local
connection configuration and appropriate database DDL permissions. It creates
the initial schema through committed migrations. Do not use production credentials
for local commands. To create a future schema change, update the Infrastructure
model, run `dotnet ef migrations add <Name>` with the same project/startup options,
and commit both migration and snapshot. Generating migrations/scripts does not
require a running database. Build/test afterward and review the generated diff.
No runtime context automatically applies migrations. The initial migration's
`Down` drops metadata tables and is destructive; do not use rollback as a retention job.

## Tests and CI

```sh
dotnet restore BetStats.slnx
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build
dotnet test tests/BetStats.IntegrationTests --configuration Release --no-build
```

Integration tests require Docker and image access. A class fixture creates a
disposable `postgres:17-alpine` container with a random password/host port and no
shared volume; connection strings from the host environment are never used.
It applies migrations to a fresh database, rolls back deterministic test data
per test and disposes contexts/transactions/container. Tests verify schema,
unique/source/run constraints, metadata round trips, UTC, append-only guards,
history deletion protection and DI without database access. Missing Docker fails
the suite; tests are not skipped. CI explicitly runs unit, architecture and
PostgreSQL integration tests on Ubuntu with Docker, preserving CodeQL.

## Reset and retention

To delete **all local development database data**, after confirming the Compose
project and that no data needs to be retained:

```sh
docker compose down --volumes
docker compose up -d --wait postgres
# Set ConnectionStrings__BetStats and apply migrations again.
dotnet ef database update --project src/BetStats.Infrastructure --startup-project src/BetStats.Infrastructure
```

Ordinary shutdown (`docker compose down`) preserves the volume. Tests do not need
this reset. Never reset a shared/production database as part of testing.

Retain permitted capture provenance for reproducibility without assuming an
unlimited right to keep payloads. Licensing/privacy deletion must later use an
explicit audited retention process coordinating storage and metadata; this task
adds no purge job. Restrictive FKs require dependent records to be deliberately
handled. Do not commit restricted provider data, local credentials or database
dumps. Use separate migration credentials/roles for future production; production
hosting and permission provisioning are outside BS-002.
