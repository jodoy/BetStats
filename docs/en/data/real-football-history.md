# BS-013: authorized local football history

## Authorization and scope

Run only in Development against an explicitly configured database and RAW directory.
No command runs at startup. Apply migrations as a separate operator action. Obtain
documented source permission before opening or copying a provider file. Technical
availability is not authorization. Create an enabled source and reviewed effective
SourcePolicy using the existing governance process; import requires retrieval,
RAW storage, historical retention and internal analytics. Missing, ambiguous,
revoked or unsatisfied restricted permission denies access. Public display,
training, redistribution and commercial use remain separate permissions. A capped
retention policy without a supported retention plan fails closed; this stage does
not schedule deletion or silently assume unlimited retention.

No provider grant or real data fixture is included. Tests use project-authored
fictional data. Do not use the synthetic setup helper to authorize real data.

## Profile football-history-v1

UTF-8 CSV, at most 1 MiB, 5,000 rows, 15 supported columns, RFC-style quoting.
Required columns: Div, Date, HomeTeam, AwayTeam, FTHG, FTAG. Optional columns:
HTHG, HTAG, FTR, HTR, Time, MatchId, Status, ResultBasis, PublishedAtUtc.
Unexpected or duplicate columns are rejected; do not silently strip columns or
claim this profile accepts arbitrary provider exports. Keep original source files
and obtain explicit authorization for any separately prepared projection.

Date must be dd/MM/yyyy. Competition and season are explicit operator context;
the reviewed canonical season interval, when available, is checked. No ambiguous
two-digit year expansion. Team references use existing competition-bound name
hashes; aliases need separate reviewed mappings. MatchId becomes provider:ID;
without it the original date/home/away composite identity is used. Different IDs
for the same dated pairing are quarantined as collisions. Conflicting duplicate
IDs remove both candidate results. Equivalent duplicates are reported.

Empty, unknown and unavailable scores remain null. Explicit Status and ResultBasis
use the case-sensitive existing football enums. Without status, paired known
scores mean Finished, otherwise Scheduled. The profile defines FTHG/FTAG as
regulation scores; a finished row defaults to RegulationTime. Use explicit
IncludesExtraTime/PenaltyShootout/Unknown for other scopes. Postponed, Cancelled,
Abandoned and extra-time results cannot create regulation-time full-time labels.
FTR/HTR, if supplied, must agree with scores. PublishedAtUtc is optional strict UTC
yyyy-MM-ddTHH:mm:ss.ffffffZ; omitted time stays null. Local file timestamps are
never publication or historical recording evidence. Time is validated but is not
converted into a guessed UTC kickoff. Partial rows are reported; unterminated
quoted files fail as malformed and retain RAW evidence.

## Operator commands

Use `DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats` and
`Ingestion__RawStoragePath`. Every action requires `FootballHistory:OperatorId`
and `FootballHistory:Reason`. Values below are placeholders, not credentials.

```powershell
dotnet run --project src/BetStats.Worker -c Release -- --FootballHistory:Action plan --FootballHistory:OperatorId operator --FootballHistory:Reason "Approved local history" --FootballHistory:SourceId SOURCE_UUID --FootballHistory:PayloadPath LOCAL_FILE --FootballHistory:Competition DIV --FootballHistory:Season SEASON
```

The returned Result is a versioned plan containing exact content SHA-256, length,
scope and fingerprint; it authorizes no mutation and performs no parsing. Pass that
Result object as `FootballHistory:PlanJson` to capture, alongside PayloadPath,
OperationId and `FootballHistory:Approve true`.

```powershell
dotnet run --project src/BetStats.Worker -c Release -- --FootballHistory:Action capture --FootballHistory:OperatorId operator --FootballHistory:Reason "Approved import" --FootballHistory:OperationId OPERATION_UUID --FootballHistory:PayloadPath LOCAL_FILE --FootballHistory:PlanJson PLAN_JSON --FootballHistory:Approve true
```

Capture retains exact bytes and original scope before parsing, then publishes only
with reviewed competition, season, both teams and event. Unknown event identities
are listed through existing Quality list/inspect/candidates/review commands. Create
the intended canonical event through existing canonical administration, review the
event mapping explicitly, then use `reconcile` with RawId, Competition, Season and
Approve. No silent event creation occurs for this profile. Inspect the retained RAW
UUID through the ingestion run/RAW database manifest and the operation AttemptId.

`inspect` accepts OperationId. `recover` accepts the same arguments as capture and
must match its fingerprint. A failed/partial attempt can be retried after review;
changed bytes require a new plan and operation UUID. A running owner requires an
expired ten-minute lease and an available source session lock. The session lock
prevents recovery overtaking a live owner. The same UUID across sources is rejected.
Repeated success returns its receipt. Publication fingerprints prevent duplicate
result versions; corrections with new bytes append evidence and correction links.
`recover-storage` with Approve finalizes only authorized hash-verified staging;
orphans require separately authorized maintenance. No scheduled retry or cleanup.

`readiness` accepts SourceId and reports bounded league/season counts, samples,
retrospective recording and explicit eligibility exclusions. It grants neither
Complete/Empty metadata nor result coverage. Missing fixture count stays unknown
without reviewed inventory. Certified usable windows remain zero until the BS-011
event-time, pre-event evidence, coverage and evaluation gates establish eligibility.
Run the existing quality report command with AttemptId to inspect row reasons;
reconciliation reports RAW corruption and unavailable evidence without publication.

## API and limitations

FootballProviderContract v1 declares local/API capabilities without granting rights.
IFootballApiTransport has no registered real implementation. FootballPagination
bounds page count/size and rejects repeated cursors. Any future implementation must
send every page and retry through AuthorizedProviderExecutor for live policy,
timeout, cancellation, request budget and 429 Retry-After handling. There are no
credentials, provider URLs, scraping, startup imports or automatic scheduling.
Operator identities are audit claims, not application authentication. Restrict
database and local filesystem access using existing operator governance.
