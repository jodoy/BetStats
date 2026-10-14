# BS-019A durable provider publication verification

Baseline: fetched `origin/main` `6d50344e82c3824d7dcf54c1c8ddbdefe9008ef6`;
PR #22 confirmed merged. Continued existing branch and [PR #23](https://github.com/jodoy/BetStats/pull/23)
from `cc51afd0c4437b212e5ea9f32564060789bf5ce2`. Original mapper, 43 cases and audit are retained.
See [ADR 0033](../../adr/0033-durable-provider-publication.md) and [exact changed files](bs-019a-changed-files.txt).

## Runtime delivered

Registered original-RAW publication service; exact profile/source/scope/hash/clocks/pinned
policy validation; strict existing reader/mapper; reviewed competition/season/home/away/event
bindings; unresolved review quarantine; conflict rejection and forward reviewed target
corrections. Append-only date/schedule/status/time observations and independent final-result
corrections preserve actual knowledge times. Missing scores remain null.

`ProviderPublications` authoritative receipts are canonical hashed manifests with unique
immutable input/decision keys. They bind original RAW, policy, mapping, canonical IDs,
observation/correction IDs, nullable source publication, actual publication/recording clocks
and parent job/execution/attempt/owner. A separate connection verifies committed references.
All eligible canonical writes, quality evidence and authoritative receipt share one
READ COMMITTED transaction (two saves, one commit). Parent acknowledgment is a separate
recoverable stage. Original RAW is never rewritten or recaptured.

Existing BS-015 scheduler, leases/live-session locks, retry budgets, cancellation and recovery
are reused. Source row locking serializes publication with policy audit/review/disable.
Application checks and a deferred database trigger recheck ownership and rights at commit,
including effective policy interval, purposes, unsupported bounded retention and attribution.
A revocation committed first denies publication; a waiting revocation follows the prior
authorized commit. This is database order, not a claim about requests outside PostgreSQL.

Development-only plan/dispatch/inspect/verify/recover use existing Worker commands; no startup
execution. Bounded Development-loopback GET monitoring and PL/EN views expose original RAW,
parsing/publication state, bindings, exclusions, corrections, attempts and receipt verification.
BS-016 validation and BS-017 acquisition/cohort contracts can reference publication executions.
Existing precise-time/result readers verify the original JSON. Coverage/warm-up/cutoff and
frozen prediction/evaluation gates remain unchanged; no models or metrics are fabricated.

## Database impact

One additive migration: `20261014115707_DurableProviderPublication`, plus generated designer
and model snapshot. It adds the receipt table, source/key unique index, restrictive RAW/policy/
execution FKs, database clock, immutability and deferred ownership/rights triggers. No legacy
row updates, provider policy seeds or destructive changes. Tests migrate both a fresh database
and the immediately preceding schema. Existing upgrade tests preserve older RAW/identity/
observation/dataset/prediction/evaluation bytes, hashes and clocks; expected migration count
advances from 14 to 15 and read-only OpenAPI routes from 20 to 22.

## Test matrix

| Requirement | Disposable PostgreSQL / automated evidence |
| --- | --- |
| Canonical fixture, precise kickoff, receipt and independent verification | Committed_fixture_receipt_is_independently_verified_and_dispatch_is_idempotent |
| Duplicate dispatch and concurrent owners | Same test plus Concurrent_jobs_serialize_on_source_and_publish_one_receipt |
| Unknown/ambiguous/conflicting identity; approved review/correction | Review_required_raw_can_be_reprocessed_after_explicit_governed_review; Ambiguous_or_conflicting_team_bindings_never_publish; Approved_event_identity_correction_publishes_forward_without_rebinding_history |
| League/season/hash/profile/source mismatch | Mismatched_immutable_input_is_rejected plus retained strict mapper cases |
| Revoked/disabled source and corrupt original RAW | Rights_and_original_bytes_are_checked_at_publication |
| Commit boundary revocation and lease | Revocation_waiting_at_source_boundary_prevents_publication; Database_deferred_boundary_rejects_expiry_or_revocation_at_commit |
| Actual retention/attribution boundary denial | Restrictions_fail_closed_without_an_implemented_satisfaction_mechanism seeds controlled ineligible RAW and proves approved jobs cannot bypass rights |
| Crash inside transaction / after canonical save | Receipt_failure_rolls_back_all_canonical_writes_and_same_raw_can_be_recovered uses PostgreSQL receipt fault injection |
| Crash after RAW capture / before mapping | Publication starts only from captured RAW; receipt tests preserve original UUID/hash/retrieval/recording clocks |
| Identity-stage interruption/review | Durable ReviewRequired receipt, existing review tool, deterministic reprocessing |
| Crash after commit before acknowledgment | Commit_without_parent_ack_recovers_from_original_raw_and_reuses_receipt invokes real Worker recover |
| Stale owner, lease expiry, cancellation, exhaustion | Lease_owner_and_cancellation_fence_all_writes; Retry_exhaustion_does_not_publish_or_fetch_new_raw |
| Status/postponement/cancellation and no fabricated labels | Status_progression_is_durable_without_fabricating_results |
| Schedule/result correction, as-of, frozen artifact integrity | Schedule_and_result_corrections_preserve_as_of_and_frozen_artifacts creates actual frozen model artifacts before JSON corrections |
| Prospective/validation and original JSON kickoff proof | Existing_validation_and_prospective_cohort_can_reference_published_raw |
| Executable Development-only Worker planning/dispatch | Development_worker_plan_and_dispatch_publish_without_provider_transport |
| Read-only PL/EN and loopback access | Monitoring_is_read_only_loopback_bilingual_and_currently_authorized; GET state fingerprints unchanged, POST 405, Production 403 |
| Receipt immutability | Receipt_cannot_be_mutated_through_direct_sql |
| Fresh/upgrade and legacy compatibility | Additive_migration_supports_fresh_creation_and_prior_schema_upgrade plus existing complete upgrade suite |
| Existing bound kickoff JSON profile compatibility | Bound_kickoff_claim_json_retains_legacy_profile_instead_of_becoming_provider_json |
| No provider HTTP / scheduler duplication / public mutation | Two new architecture tests plus retained BS-018 boundary tests |

Local full suite: **929 passed, 0 failed, 0 skipped**: 358 unit, 26 architecture and 545 integration.
Integration took 6 minutes 34 seconds against disposable PostgreSQL 17 Alpine. All original
844 tests remain, plus 44 mapping/profile cases, 39 publication/recovery PostgreSQL cases and
two architecture cases. The original 43 preparatory mapping cases are retained.
Locked restore and Release build passed with zero warnings/errors; EF reports no pending
model changes. Diff checks passed. Exact final-head CI/CodeQL results and commit are recorded
in [PR #23](https://github.com/jodoy/BetStats/pull/23); an earlier green SHA is never substituted.

## Verification commands

```powershell
dotnet restore BetStats.slnx --locked-mode
dotnet build BetStats.slnx --configuration Release --no-restore
dotnet test BetStats.slnx --configuration Release --no-build --logger 'trx;LogFilePrefix=bs019a-final' --results-directory .artifacts/bs019a-final
dotnet ef migrations has-pending-model-changes --project src/BetStats.Infrastructure
git diff --check
```

## Acceptance and limits

Runtime publication, authoritative receipts, atomic child transaction, governed identity,
idempotency, fencing, original-RAW recovery, commit rights/integrity, correction history,
frozen artifact preservation, prospective report links, Development Worker and read-only
PL/EN monitoring are implemented. Final acceptance additionally requires green complete
regression, EF/diff checks and exact final-head CI/CodeQL; PR remains Draft until those pass.

No live provider transport, credentials, acquisition activation, real-source compatibility,
independent completeness or operational accuracy is certified. Exact existing canonical
bindings are required; source-policy scope/rights and operator claims are not invented.
Unknown and unsupported status/result bases fail closed with explicit exclusions. Retention
and attribution without a supported satisfaction mechanism are denied. Receipt history
records database-ordered authorization; it does not retroactively undo legally published data.
A privileged out-of-protocol filesystem mutation is detected on verification/read; no claim
is made that PostgreSQL locks prevent an administrator from damaging external files.
No missing BS-019A functionality is assigned to BS-019B. No automatic merge is permitted.
