# Provider publication recovery and failure matrix

The canonical transaction and parent acknowledgment are distinct durable stages.
`Published` in a verified child receipt establishes committed observations, not a completed
parent execution or prediction eligibility. A blocked partial batch lists which fixtures
were excluded; all eligible writes and the authoritative receipt share one transaction.

| Interruption/failure | Durable state | Explicit action / expected result |
| --- | --- | --- |
| After original RAW capture, before mapping | Original manifest/bytes/clocks | Plan/dispatch using the same RAW UUID |
| During parsing or before publication | No new canonical rows | Correct input/context through approved new evidence; retry unchanged original RAW only when valid |
| During unresolved identity resolution | ReviewRequired receipt and unresolved anchors, no guessed targets | Inspect/review existing identities, then retry; decision versions produce a new receipt |
| During canonical/receipt transaction | All transactional writes rolled back | Retry/recover same RAW after cause is resolved |
| Receipt insertion fails after canonical save | No canonical/time/result/receipt escapes rollback | Retry original RAW |
| Commit succeeds, acknowledgment is lost | Immutable authoritative receipt; parent Running | After owner death/lease expiry, recover reuses exact receipt |
| Duplicate dispatch or concurrent jobs | Source serialization and unique immutable key | Same receipt and observation IDs; parent acknowledgments reference the original publisher |
| Lease expires/stale owner/cancellation before commit | No publication | Application checks plus deferred commit fence reject writes |
| Cancellation/revocation after valid child commit | Child remains immutable; acknowledgment may be denied/cancelled | Inspect child and parent separately; never erase history or claim whole-operation atomicity |
| Final permitted attempt expires | Blocked / attempt_budget_exhausted | No additional execution or provider request |
| Revocation wins source lock | No publication | Restore lawful approval through governance; do not bypass policy pinning |
| Publication wins source lock | Authorized transaction commits before waiting revocation | Later reads/publication enforce revoked rights; historical receipt is not rewritten |
| Missing/unsatisfied retention or attribution | Denied | No invented satisfaction mechanism or approval |
| Missing/corrupt original RAW | Failed/blocked; no fabricated evidence | Restore legally retained exact original bytes or keep unavailable |
| Unsupported final basis/missing scores | Metadata can publish with explicit result exclusions | Do not fabricate zero scores or labels |
| Schedule/score correction | Append-only observations with original clocks and predecessor IDs | As-of before correction sees only earlier evidence |
| Approved canonical target correction | Newer RAW required for forward publication | Old receipt/target and frozen artifacts remain unchanged |

PostgreSQL tests exercise capture-to-publication, governed review/retry, transactional
fault injection at receipt insertion, post-commit lost acknowledgment through real Worker
recover, concurrent independent contexts, revoked source locking, cancellation, exhaustion,
commit-time deferred fencing, corruption, score/time corrections and independent verification.
The database migration is exercised both from empty and the immediately preceding schema;
existing migration tests retain RAW, observations, predictions and evaluation bytes/hashes/clocks.
Controlled JSON proves software behavior only. It establishes no real provider authorization,
coverage, compatibility or performance. Runtime never makes provider HTTP calls.
