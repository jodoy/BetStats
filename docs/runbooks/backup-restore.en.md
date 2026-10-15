# Development PostgreSQL + RAW backup and restore

Scope: PostgreSQL 17, local Docker and PowerShell 7. Never run this procedure
against production or while application/maintenance processes are writing.
Backup content is private provider data: keep it outside Git, encrypt exported
copies, restrict access and apply the approved retention policy to copies too.

## Recovery point and limits

Stop API, Web, Worker, migration processes and all RAW maintenance. Confirm no
other database writers or schema administrators are active. `OfflineConfirmed`
is an operator assertion, not automatic process discovery. Database SHARE locks
block DML on existing tables for the snapshot/dump/copy interval; they do not
protect against a privileged administrator changing schema or external files.

The backup uses one exported RepeatableRead snapshot for both manifests and
pg_dump. Snapshot ID defines the database recovery point. SnapshotUtc is an
observation timestamp, not an assertion that every commit before that wall-clock
time is included. Original referenced files are immutable and SHA-256 verified.
Referenced `.pending` originals are preserved as `.raw` in the copy; source files
are never renamed/deleted. Unreferenced staged files are not certified by the DB
snapshot and are left untouched in the source. Uncertain attempts remain uncertain.

RPO is the age of the latest valid snapshot plus the chosen backup interval;
there is no automatic scheduler or zero-loss promise. RTO depends on dump size,
disk and verification/recovery. The disposable synthetic script drill took about
21 seconds in the initial local test, not a production RTO guarantee. Measure a
representative restore before relying on an operational objective.

## Backup

Build Release first. In a private PowerShell process set administrative
`ConnectionStrings__BetStats` and `Ingestion__RawStoragePath` using local secrets.
Never put passwords in command arguments, tracked config or terminal transcripts.
The container/user must identify the SAME database cluster as the connection.

```powershell
./eng/backup.ps1 -Container YOUR_POSTGRES_CONTAINER -User betstats_admin `
  -Destination "$env:LOCALAPPDATA/BetStats/backups/NEW_BACKUP_NAME" -OfflineConfirmed
```

The destination must be a new absolute directory outside Git, without links.
The engine restricts its permissions (Windows owner-only; Unix 0700), locks tables
with a 10-second lock timeout, exports a snapshot, copies referenced RAW, creates
`database.dump`, then validates lengths/hashes and writes manifest.json + COMPLETE.
Database dump and manifest have SHA-256 hashes. COMPLETE seals the manifest.
They detect corruption/incomplete copies, not a malicious owner rewriting hashes.
Keep the directory private throughout transfer. Missing lengths in legacy RAW
manifests fail closed; investigate through an approved compatibility procedure.

Exit failure, missing COMPLETE, missing files, altered hashes, duplicate IDs/keys
or DB/RAW disagreement invalidate the copy. Do not label such a directory a backup.
Do not delete source originals or pending files to make validation pass.

## Restore drill

```powershell
./eng/restore.ps1 -Backup "$env:LOCALAPPDATA/BetStats/backups/VALID_BACKUP_NAME" `
  -Destination "$env:LOCALAPPDATA/BetStats/restore/NEW_DRILL_NAME"
```

The script verifies the seal/files BEFORE restore, creates a NEW disposable
PostgreSQL 17 container (random password, loopback/random port, no volume), copies
the bundle into a new protected directory and runs pg_restore with exit-on-error.
It verifies exact RAW IDs, source/run bindings, keys, lengths and hashes against
the restored DB and checks EF model consistency. pg_restore recreates database
constraints/dependencies. No live acquisition or migrations execute automatically.
The script sets the enable flag false and changes connection/RAW environment
variables ONLY in that PowerShell process to the isolated target. It does not
repoint an already running API/Worker or the live database.

Inspect the returned container/path. Configure restricted roles before runtime
use. Explicitly review recovered job/owner state, current policies and receipts
using existing inspect/recover/publication commands. Never clear an uncertain
reservation to redownload; recovery must use original RAW or remain blocked.
The integration drill proves receipt replay without provider requests.

Containers are retained by the operator script for inspection; remove only the
exact printed disposable container after review. Restore never overwrites an
existing destination or original DB. The automated test cleans up its own targets.

## Incident procedure

Stop writers, preserve the failed bundle and source disks, record safe job/run/RAW
IDs and failure category. Select the last independently verified sealed copy.
Restore into isolation and verify before any cutover. Missing/corrupt originals
must remain unavailable; preserve receipts/frozen artifacts and investigate rather
than modifying hashes, fabricating success or requesting provider data again.
No automatic cutover, application retargeting or file cleanup is provided.
