# BS-019A pre-implementation architectural audit

Baseline: fetched `origin/main` `6d50344e82c3824d7dcf54c1c8ddbdefe9008ef6`.
GitHub PR #22 is merged (2026-10-13T16:47:14Z), with that merge commit.
Reviewed ADR 0032 and BS-018 verification before making implementation changes.
Existing untracked Web launch settings are operator-owned and excluded.

## Reuse and gaps

| Boundary | Existing implementation | Publication requirement |
| --- | --- | --- |
| Provider parsing | `ApiFootballFixtureReader`, explicit canary, duplicate-member/fixture rejection, UTC/unix agreement, bounded single-page response | Reuse this reader; preserve provider status and distinguish metadata blockers from result eligibility |
| RAW | `FootballIngestion`, `FileSystemRawPayloadStore`, `RawPayload`, `FootballRawContext` | Keep original bytes, SHA-256, source/scope/profile, retrieval and database recording clocks; recovery must read this manifest |
| Canonical entities | `Sport`, `Competition`, `Season`, `Participant`, `SportingEvent`, `EventParticipant` | Reuse existing entities; no provider IDs on canonical records |
| Identity | `ProviderIdentity`, append-only `IdentityResolution` and explicit operator review | Require resolved source-scoped decisions and validate sport, season, participant roles and event context; never name matching |
| Observations | Append-only `Observation` with correction chains; `ScheduledStart`, `EventDate`, `EventStatus` already exist | Reuse precise scheduled-start stream rather than adding a new time model |
| Results | `FootballResultObservation`, independent version/correction chain and eligibility rules | Only independently supported regulation-time final scores; missing half-time data must remain nullable |
| Publication | `FootballIngestionPersistence`, source lock and atomic observation/result/receipt writes | Current accepted profiles are CSV only. Existing receipt lacks policy/identity/mapping/pipeline context. Do not enable JSON by simply adding its profile to the allowlist |
| Jobs | BS-015 `PostgreSqlPipeline`, immutable definition, attempt budget, owner UUID, lease, cancellation, explicit recovery | Extend this scheduler; check and lock current ownership at publication, including commit-time lease/cancellation checks |
| Import recovery | `FootballImportOperations` and `IngestionRecovery` | Existing local import reopens the operator file; this is insufficient for original-RAW-only JSON recovery |
| Time evidence | `EventTimeEvidence` and BS-013 coverage operations | Precise observation alone does not establish independent coverage or prospective eligibility |
| Validation/prospective | BS-016 validation and BS-017 acquisition/cohort reports | Preserve evidence kind and all current cutoff, warm-up, rights and frozen-before-kickoff checks |
| Monitoring | Development-loopback read-only dashboard and PL/EN acquisition views | Add bounded receipt projections only after durable publication exists; GET must perform no work |

## Findings that constrain implementation

`FootballMatchRecord` carries a date but no precise kickoff. Existing CSV publication
only creates previously unmapped events with explicit completion; historical imports
require reviewed decisions. Neither behavior can be silently generalized to API JSON.
Current batch idempotency binds source, scope, hash and parser version, but does not
bind policy or identity decision versions. A new JSON mapping must explicitly bind
those immutable inputs and must not reinterpret existing receipt keys.

RAW capture and canonical publication are separate transactions. Recovery after capture
must reuse the original manifest and clocks, while recovery after canonical commit must
reuse a durable receipt even when the parent acknowledgment was interrupted. Recovery
must not call the provider or recapture the bytes with a new retrieval time.

No approved real API-Football policy, entitlement or credentials were provided. Controlled
fixtures may prove software behavior only. No live HTTP registration, provider activation,
startup migration, public mutation endpoint or real-data readiness claim is authorized.

## Verification environment

.NET SDK 10.0.401 is installed. Initial sandboxed Docker access was denied; disposable
PostgreSQL verification requires checking access under the permitted execution profile.
No test result is inferred from BS-018's green checks. BS-019A must record its own exact
test counts and final-head CI/CodeQL results.
