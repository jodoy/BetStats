# Provider quota, recovery and credentials

## Quota and transaction order

Reservations lock job → execution → source and use the PostgreSQL recording clock.
The reservation and ingestion run commit before HTTP. Count every source reservation
against the grant's explicitly approved total ceiling (maximum 100). Failed, timed
out, cancelled and uncertain requests consume quota; no refunds. Reactivation,
new processes/grants and midnight never reset usage. A reviewed increase changes
the total ceiling, not the count. Enforce one in-flight request, minimum interval
and persisted Retry-After across contexts. Provider-side remaining quota is Unknown.

Immutable SQL history triggers reject UPDATE/DELETE/TRUNCATE for grants, activation
events and request events. Recording clocks come from DB triggers. Deferred request
constraints recheck the owner/attempt/live lease/cancellation, activation, effective
policy, purposes, obligations, quota and outstanding request at commit. RAW capture
reuses the source-locked ingestion transaction with job/execution fencing and pinned
activation/policy checks before save and commit. Provider staging retains this same
source lock, closing the revocation gap between authorization and filesystem use.
Original RAW bytes are staged and
fsynced before its database manifest; finalization is recoverable and idempotent.

Disable commits under the source lock and blocks subsequent reservations. A
previously reserved request is already in flight; it cannot be recalled reliably.
The workflow checks source rights and activation again before capture. A disabled
or revoked in-flight response is discarded rather than persisted without rights.
Its conservative reservation remains consumed/uncertain. No network/DB atomicity
or exactly-once provider delivery is claimed.

## Failure and recovery

| Boundary | Durable evidence | Recovery |
| --- | --- | --- |
| Before reservation commit | None | Existing bounded job retry may reserve once. |
| After reservation / after HTTP before RAW | Consumed reservation; response uncertain | No automatic HTTP. Finish/cancel/recover the BS-015 execution, then explicit reconciliation if no RAW exists. |
| HTTP 401/403/429/5xx/timeout/size failure | Consumed reservation and sanitized failure | A different explicit job may request again after quota/Retry-After checks; recovery of the same execution never re-downloads. |
| RAW committed, finalization or acknowledgment interrupted | Original RAW linked through durable ingestion run | Recover existing execution; find RAW by run UUID, verify/finalize original bytes, prepare publication. |
| Invalid JSON/duplicates/scope/pagination | Original RAW, parser blocker | Inspect safely; no canonical publication, no automatic new request. |
| Publication preparation before acknowledgment | RAW plus deterministic disabled publication job | Reuse identical job/RAW; do not overwrite a revised job. |
| ReviewRequired | RAW, reviewed/unresolved anchors, immutable receipt | Review exact canonical targets and retry publication only. |
| Canonical publication committed before acknowledgment | BS-019A immutable receipt/observations | Existing publication recovery reuses receipt; no HTTP. |
| Attempt budget exhausted | Existing BS-015 Blocked audit | Stop; do not expand attempts/scope automatically. |

Use `retry` for terminal executions, `recover` only for an expired Running execution
with no live owner session. Both retain the existing bounded attempt budget and
owner/session fences. Same-execution HTTP is never retried after a reservation.

To acknowledge an uncertain call **without refunding it**, only after the execution
is terminal and the operator has investigated the external outcome:

```powershell
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-reconcile-uncertain --Pipeline:ExecutionId='<actual execution UUID>' --Pipeline:Actor=operator:local --Pipeline:Reason='Investigated uncertain request; acknowledge consumed budget without RAW' --Pipeline:Approve=true
```

Reconciliation is rejected while Running or when original RAW exists. It appends
request evidence and the existing immutable pipeline actor/reason audit. New calls
still require another explicitly dispatched job and unused approved budget.

## Safe disable and rotation

```powershell
dotnet run --project src/BetStats.Worker -c Release --no-build -- --Pipeline:Action=provider-disable --Pipeline:GrantId='<actual grant UUID>' --Pipeline:Actor=operator:local --Pipeline:Reason='Stop bounded canary' --Pipeline:Approve=true
$env:BETSTATS_API_FOOTBALL_ENABLED = 'false'
Remove-Item Env:BETSTATS_API_FOOTBALL_KEY -ErrorAction SilentlyContinue
```

Database disable does not require an available key, runtime enable flag or current
retrieval rights. It requires Development and explicit operator approval. Cancel
unwanted existing jobs separately with BS-015 commands; disable does not erase
in-flight evidence. Revocation/source disable use the established governance process.

Rotate the environment key privately and restart the intended Worker environment
if needed; activation/scope/budgets and historical RAW/receipts remain unchanged.
Never include key values in grants, appsettings, HTTP diagnostics, reports, exceptions,
dashboard responses or CI. The pooled handler has no redirects/cookies/proxy and no
logging handlers; application diagnostics contain only fixed error categories. Keys
are supplied per request and not serialized. The local OS/process administrator is
part of the trust boundary; environment secrets are not protection against that actor.

An optional Development-only key file is supported at
`%LOCALAPPDATA%\BetStats\secrets\api-football.json`, outside Git, with exactly one
`ApiKey` string property. Environment takes precedence. Restrict file access to the
operator; it is plaintext. The reader ignores it in Production and denies malformed,
oversize, linked or repository-contained files without secret-bearing errors.
Deleting the environment variable does not remove this fallback: delete the local
file too when retiring a temporary key. File content cannot enable the transport
or grant authorization. See the configuration guide for the empty template.

SQL operator access, existing SourcePolicy review and grant document review remain
trusted operator boundaries. The app verifies evidence bindings and approvals; it
does not claim independent legal/account certification. No public mutations, startup
HTTP/migrations, provider discovery, scraping or production activation are supported.

The default network transport requires OperatorDeclaredReal and rejects explicitly
synthetic grant/policy evidence. Synthetic transport injection exists only in test
code, has no Worker configuration switch and cannot certify Operational status.
The safe monitor reads an existing immutable prospective report bound to the latest
RAW; it never creates one or executes a model. A missing report remains unavailable.
