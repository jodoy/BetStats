# BS-014 verification

Baseline: merged PR #17, `origin/main` d91482cfe3a8b2c31dab9e677da8c04b24d71c12. PR #17 was confirmed merged, with successful CI run 37920768778 and CodeQL run 37920768847 on its actual final head 0bedfb49c27447517e639b46f7bf94e9600ccb1f. Branch: `feat/bs-014-web-dashboard`. This task never merges a pull request.

Environment: Windows, .NET SDK 10.0.401, Docker 29.6.1, disposable PostgreSQL 17 Alpine test containers. Existing user Web launch settings are excluded. No real provider HTTP requests, imports, source rights changes, training or migrations against a user database were performed.

## Commands

```powershell
git fetch origin
git switch -c feat/bs-014-web-dashboard origin/main
dotnet restore BetStats.slnx
dotnet build BetStats.slnx -c Release --no-restore
dotnet test BetStats.slnx -c Release --no-build --logger 'trx;LogFilePrefix=bs014-final' --results-directory .artifacts/bs014-final
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure --configuration Release --no-build
git diff --check
```

## Local evidence

Final test results are recorded after the final delivery run. EF reports no model changes; all existing 13 migrations remain unchanged. Release compilation has no warnings or errors.

Added verification covers loopback/Host/Origin restrictions, unsafe API URLs, bounded filters, unavailable/ineligible metric formatting, explicit fictional DEMO semantics, localized Blazor loading/empty/denied/error states, PostgreSQL pagination and ordering, display permission separation, revocation and changed attribution/retention restrictions, missing/corrupt RAW, safe HTTP DTOs, GET-only routes and immutable artifact bytes/hashes/timestamps with unchanged operation ledgers. Architecture checks reject persistence access from Web and execution/mutation calls from the query adapter.

Browser verification used the local API presentation DEMO and Blazor host: English and Polish navigation, six fixture states, a known regulation score, Completed filtering, model empty state with DEMO banner and a 390×844 mobile viewport. The temporary viewport was reset. No fabricated probabilities or observed accuracy were supplied by DEMO.

An initial PostgreSQL attempt found Docker stopped; Docker Desktop was started and the tests were rerun. An initial complete suite exposed a pre-existing two-second inventory-expiry race under concurrent container load. The test now gives publication/review 15 seconds and waits until the database expiry time before asserting Expired; production expiry rules are unchanged. Intermediate failed runs are not delivery evidence.

## Semantics and limits

Only stored governed evidence is displayed. Snapshot metadata status filters and historical cutoffs remain distinct from separately verified current result observations. Report metrics preserve the original sample cohort and nullable/minimum-sample semantics. Equivalent-contract sample intersections use stored eligible IDs without model execution, metric recomputation or rankings.

Quality summarizes the first 100 filtered fixtures. Source-wide inventory completeness, identity/correction coverage, pre-event evidence and readiness are explicitly uncertified rather than inferred from an import or snapshot. Attribution-restricted sources fail closed. Models and predictions are available only through existing finalized artifacts; empty installations and DEMO have no invented model outputs.

Stored integrity checks verify canonical artifact hashes, current rights/retention, retained RAW hashes, feature-dataset bindings, frozen policy audits and score-distribution/model/input bindings. They do not claim independent model replay. Web references Application only, and every dashboard HTTP route is a bounded read. No database schema, immutable manifest contract or deployment workflow is changed.

## Remote checks

The pull request description records CI and CodeQL run links against the exact final head. Workflow success must be confirmed on that head before delivery; an earlier-head success does not count. No merge is performed.
