# ADR 0029: Explicit durable governed orchestration

Status: Accepted

## Context

BS-015 requires opt-in scheduling without startup execution, invented acquisition, retrospective prediction claims or changes to finalized historical artifacts.

## Decision

Use PostgreSQL job heads, immutable versioned canonical definitions, execution heads and append-only receipts. Acquisition uses the database clock, row locks, leases and owner tokens. Explicit recovery also requires the execution advisory lock to be free; a live worker is never taken over merely because a lease expired. Stable child operation IDs reuse the BS-013 import and BS-010/011 publication ledgers. Fenced completion and content-addressed outputs make retries idempotent. Cancellation is cooperative; lost database access cannot manufacture a successful receipt.

Schedules accept UTC instants only, with an explicit UTC timezone and no-DST policy. Recurrence is anchored to planned instants; missed occurrences are skipped, not replayed as historical execution. Operators explicitly plan, enable and invoke a bounded run-once/acquisition loop. No hosted scheduler, startup migration or public administrative endpoint is added.

Local synchronization binds an explicitly authorized source, file hash, original scope and BS-013 import plan. HTTP transport remains unsupported. Prematch execution verifies source-bound precise kickoff, uses the actual database execution cutoff and records planned cutoff/lateness separately. An optional versioned metadata target-time policy (`source-bound-kickoff-v1`) permits a same-day target only with one precise, admissible source-bound kickoff frozen at cutoff. Default/null definitions retain their original calendar-day rule and omit the new property from serialization, preserving existing bytes. History remains restricted to prior calendar days; the policy does not admit future or same-day results. Existing model and immutable prediction publication contracts remain intact. Postmatch evaluation consumes frozen predictions through the existing v3 eligibility and metric machinery, never a live predictor; a separate immutable pipeline output binds the original artifact hash.

Audit receipts are durable and bounded per execution by retry limits. Operational diagnostics are separate, sanitized and pruned by count and age. Health reports infrastructure availability separately from uncertified provider/data readiness. Actor/reason fields are operator audit claims, not authentication.

## Consequences

New scheduler tables require explicit fresh/upgrade migrations. Existing manifests and finalized bytes remain unchanged. Incomplete kickoff, history, coverage, rights or event-end evidence creates blocked/excluded receipts and unavailable metrics. Neither a successful import nor a synthetic workflow certifies operational accuracy. Remote controls, provider HTTP and automatic startup execution remain out of scope.
