# Football result observations and observed historical statistics (BS-009)

Read [ADR 0023](../../adr/0023-football-results-and-outcome-provenance.md) with
[the historical integrity baseline](historical-integrity-development-api.md).

## Supported evidence

`results-v1` is an explicit, project-authored fictional CSV contract, separate from
the unchanged `metadata-v1` parser. Columns are `Div,Date,HomeTeam,AwayTeam,MatchId,
Status,ResultBasis,FTHG,FTAG,HTHG,HTAG,PublishedAtUtc` (header ordering may vary).
The file's competition/season scope is captured immutably before parsing. Explicit
reviewed competition, season and participant identities are still required.
No live transport or candidate provider rights are enabled.

Statuses: Scheduled, Live, HalfTime, Finished, Postponed, Cancelled, Abandoned.
Supported regulation result periods: HalfTime and FullTime, represented by distinct
score pairs. Scores are nonnegative int32, with total bounded by int32. A pair is
either entirely known or entirely null; blank, `unknown` and `unavailable` remain
null. Missing half-time values never acquire an inferred score. Finished with
missing full-time scores is retained but cannot supply result features or labels.
`Unknown`, `IncludesExtraTime` and `PenaltyShootout` bases are retained as evidence
but cannot supply regulation-time outcome labels. No extra-time periods are invented.

Publication is an optional UTC microsecond source claim, never a substitute for
retrieval, consumer availability or trusted PostgreSQL recording. Dates remain
DateOnly; they do not imply kickoff/end instants. The fixed fixture is a fictional
2026 timeline, including a future scheduled target, not evidence about actual games.

## Validation, corrections and history

Quality rules v1 record stable `result_invalid_goals`,
`result_halftime_exceeds_fulltime`, `result_invalid_status_transition`,
`result_conflicting_report`, `result_stale_report`,
`result_publication_after_retrieval` and parser reason codes. Assessments append to
the existing ledger. Invalid rows retain RAW/audit evidence without normalized facts.
The original metadata quality rules and publication receipts continue to apply.

Same-source legitimate later reports append a version/predecessor chain. Changed
values require later retrieval and, when both publication claims exist, later
publication. Simultaneous contradictory values remain a conflict; terminal Finished
or Cancelled cannot revert to Scheduled. Equivalent rows/imports are idempotent.
For compatibility with existing metadata publication, Postponed must return to
Scheduled before Live/HalfTime; Abandoned is terminal except an explicit Cancelled
report. A new completed report may follow Postponed, with the usual score gates.
Independent sources disagreeing on a canonical event have no automatic winner.
Reconciliation verifies original context and cannot reinterpret a file's season.

`IFootballResults.ReadAsync` requires explicit competition, season, purpose/context
and AsOfUtc. AvailableAtUtc and RecordedAtUtc must both be <= AsOfUtc. Identity,
quality and policy interpretation uses T, or explicit reconstruction R; reconstruction
cannot change the result's frozen identity/scope silently. RAW hash/length, original
context and current source enablement/use/retention are checked on every query.
Queries fail rather than truncate above 1000 observations; narrow the scope.
History mode can return earlier versions known at the cutoff. Results report
eligibility/reasons internally; APIs return only permitted sanitized fixture values.
INSERT recording is not COMMIT availability (ADR 0017).

## Features, labels and datasets

Result feature/manifest schema v3 is stored in `datasets.ResultArtifacts`, separate
from existing finalized v1/v2 snapshots. `IFootballResultDatasets.BuildAsync` accepts
a metadata v2 build request plus a label cutoff between prediction and dataset AsOf.
The artifact freezes the metadata/coverage manifest and hash, result observations,
original RAW/context, time-bounded identity, quality and policy evidence, exact
feature inputs, separate label evidence and derived label references/availability.
Both halves of assembly use the same repeatable-read visibility; if an earlier
metadata build differs, the result build fails and the caller must retry completely.
Source locks revalidate current rights and metadata coverage before publication.

Features use `[UTC prediction day - 30 days, UTC prediction day)`, completed
regulation-time results known by the prediction cutoff, excluding target/same-day
and future events. Each target participant receives observed matches, wins/draws/
losses, goals scored/conceded/difference, points per match, over-2.5/BTTS frequency
and home/away points per match. Ratios freeze exact integer numerators/denominators.
No eligible history yields null with a reason; a missing venue denominator is null.
Partial observed samples are explicitly partial. Metadata coverage evidence does
not certify full result coverage, full seasons, population frequency or actual form.

Labels are HomeWin/Draw/AwayWin, FullTimeTotalGoals, BothTeamsScored, Over2_5Goals
and HalfTimeTotalGoals only with supported scores. Confirmed HalfTime may supply
only HalfTimeTotalGoals while full-time labels remain null; it cannot enter completed
match features. Cancelled/postponed, unknown
or non-regulation results cannot yield labels. A target label may arrive after
prediction and be frozen at LabelAsOfUtc; it cannot enter feature inputs/hash.
Feature hashes exclude target label evidence; the manifest hash includes both.
Repeated frozen evidence yields the same content key. Verification recalculates
features/labels from frozen values and checks integrity/current authorization;
this is not model evaluation or an assurance that RAW still exists.

## Local fictional demonstration and Swagger

Configure PostgreSQL (`ConnectionStrings__BetStats`) and an absolute RAW directory
outside the repository (`Ingestion__RawStoragePath`). Credentials stay local.
Apply migrations explicitly; hosts never migrate or seed on startup:

```sh
dotnet ef database update --project src/BetStats.Infrastructure
```

In PowerShell:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/BetStats.Worker --configuration Release -- --Results:Action=demo --approve-synthetic
dotnet run --project src/BetStats.Api --launch-profile http
```

The demo explicitly approves only the dedicated project-owned source's retrieval,
storage, retention, internal analytics and PublicDisplay permissions. It imports
Finished 2:1 (half-time 1:0), 0:0, unknown half-time, postponed/cancelled/scheduled
rows, then a later 2:2 correction, repeating each payload to demonstrate reuse.
The demo prints durable import reports and does not train or score anything.

Swagger: `http://localhost:5000/swagger`. Existing production-safe reads remain
`/health/live`, `/api/v1/health`, `/api/v1/sports`. Development-only additions:

| GET route | Response |
|---|---|
| `/api/v1/dev/events` | Paginated fictional events |
| `/api/v1/dev/events/{id}` | One fictional event |
| `/api/v1/dev/events/{id}/result` | Status, score pairs, supported labels |
| `/api/v1/dev/events/{id}/history` | Paginated historical values |

All require `asOfUtc`, e.g. `2026-10-08T18:00:00.000000Z`. Lists/history accept
`offset` (default 0, maximum 10000), `limit` (default 20, 1..100). Ordering is event
UUID, then availability for history. Invalid queries return 400 ProblemDetails;
absent/ineligible single events return 404; policy/retention denial returns 403
without values; missing development configuration returns
503; no administrative mutations are mapped. All four routes and Swagger return
404 in Production. Exact registered fixture hashes and PublicDisplay are both
required; a source name alone never grants disclosure. RAW paths, provider references
and internal quality/policy/operator audit metadata are omitted.

## Migration and limitations

`20261008171605_FootballResultsOutcomeProvenance` adds `football.Results` and
`datasets.ResultArtifacts`, RESTRICT FKs, correction uniqueness, JSON/temporal/context
validation, trusted insert clocks and UPDATE/DELETE/TRUNCATE rejection triggers.
Published migrations and finalized snapshot bytes are unchanged. Non-owner runtime
roles remain mandatory; owners can bypass guards. Back up and inspect SQL before
applying to an existing database. Rollback removes new result history intentionally.

The v3 builder is an application port; no administrative HTTP/CLI dataset build
surface is introduced. Metadata builds keep their durable attempt ledger; v3 assembly
errors are returned to its caller, without a separate result-build recovery ledger.
There is no result-completeness certification, live provider, authentication, training,
model scoring or production UI. BS-010 should prioritize governed result inventory/
coverage evidence, an operator v3 dataset command and attempt recovery, explicit
event-end evidence before a real evaluation executor, and fixture timelines that
remain deterministic beyond 2026.
