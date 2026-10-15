# PostgreSQL Development roles

The Compose POSTGRES_USER is the bootstrap superuser. It is not the recommended
runtime identity. Runtime connection configuration is read by AddPersistence
from ConnectionStrings:BetStats (environment `ConnectionStrings__BetStats`);
the design-time factory uses the same key. API launch settings contain local host
configuration; Web uses a loopback API URL and has no direct database dependency.
Testcontainers always uses disposable synthetic credentials. No operator
connection-string/password values were inspected or included in this report.
The actual operator shell may differ from the agent process: its current DB role
is not certified by successful synthetic tests.

## Existing environment upgrade

1. Stop all hosts, migrators and RAW maintenance. Take/verify a DB+RAW backup.
2. In a protected local PowerShell process set bootstrap administrative
   `ConnectionStrings__BetStats`, plus distinct randomly generated passwords
   `BETSTATS_ADMIN_PASSWORD` and `BETSTATS_APP_PASSWORD` (at least 24 characters).
   Use a secret manager/Read-Host; do not place them in command arguments or Git.
3. Run the explicit operation:

```powershell
./eng/configure-roles.ps1 -OfflineConfirmed
```

4. The transaction creates/updates betstats_admin and betstats_app, rejects app
   memberships, transfers ownership of this DB/application schemas/tables/functions
   and EF history to admin, revokes public CREATE/TEMP and previous explicit runtime
   grants, then grants runtime usage,
   SELECT/INSERT/UPDATE/DELETE and sequence USAGE/SELECT. Immutable-table triggers
   still reject forbidden DML. Runtime cannot CREATE/ALTER/DROP/TRUNCATE, own tables,
   change replication mode, create roles or disable triggers.
5. Change runtime ConnectionStrings__BetStats to betstats_app. Use betstats_admin
   only in a separate explicit EF migration/backup session. Admin has CREATEDB for
   Development administration, but no SUPERUSER/CREATEROLE/REPLICATION/BYPASSRLS.
6. Verify current_user and pg_roles in a protected session, then run an isolated
   ingestion/publication/recovery smoke test. Do not enable a real provider request.

No roles/ownership migrate at application startup. This is cluster administration,
not an EF model change; historical schema/enum values and 16 migrations remain.
Future tables/sequences created by admin inherit runtime grants. Re-run provisioning
after a reviewed schema change to reconcile ownership and existing grants.
Unexpected ownership, inherited/public CREATE or TRUNCATE privileges abort the
transaction rather than certify an existing powerful identity. Default admin-created
table/sequence grants are also reset to the reviewed runtime set.
Use a dedicated Development cluster; role names are cluster-wide. Reject unexpected
memberships rather than broadening permissions to bypass an error.

Rollback: keep the protected bootstrap identity while validating. If provisioning
fails the transaction rolls back; restore into isolation before choosing a cutover.
Do not give runtime superuser privileges to work around failing triggers. Credential
rotation and process/environment retargeting remain explicit operator actions.
