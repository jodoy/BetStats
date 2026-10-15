# API-Football Development canary configuration

BS-019B is disabled by default. No actual operator grant/key/scope was supplied in
implementation: **LIVE_CANARY_BLOCKED**. Never copy synthetic test grants into a
real source. Public [v3 documentation](https://api-sports.io/documentation/football/v3)
describes the technical endpoint, not operator rights.

The only supported request is HTTPS `v3.football.api-sports.io/fixtures` with
`league`, `season`, `from`, `to`, `timezone=UTC`. One league/season, at most seven
inclusive calendar days, one response page, 1 MiB, 500 fixtures. No undocumented
page parameter: multi-page responses block publication and remain original RAW.
No discovery/status/odds/logos/statistics/prediction endpoints or arbitrary URL.

## Operator-owned environment

Set `DOTNET_ENVIRONMENT=Development`, `ConnectionStrings__BetStats` and
`Ingestion__RawStoragePath` through the established local configuration. RAW must
be outside any repository. Apply reviewed migrations explicitly; startup never
migrates or acquires data.

After explicitly reviewing the target Development connection and migration:

```powershell
dotnet ef database update --project src/BetStats.Infrastructure
```

`BETSTATS_API_FOOTBALL_KEY` supplies the key with precedence over the optional local
file below. Only `BETSTATS_API_FOOTBALL_ENABLED=true` enables runtime transport;
enablement remains environment-only. No key is read from tracked appsettings,
command-line arguments, grant files or dashboard requests. The host's actual
environment also must be Development.
Key characters: ASCII letters/digits/hyphen/underscore, at most 256 characters.

For an interactive local PowerShell session, enter the key privately:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$localKey = Read-Host 'Operator API key (local hidden input)' -AsSecureString
$env:BETSTATS_API_FOOTBALL_KEY = [Net.NetworkCredential]::new('', $localKey).Password
Remove-Variable localKey
$env:BETSTATS_API_FOOTBALL_ENABLED = 'true'
```

Prefer an approved local secret manager injecting the environment. Do not place
keys in chat, source code, shell arguments/history, issue bodies, screenshots or
CI variables for automated tests. Setting these variables performs no request.

### Optional local Development file

Worker and API can read `%LOCALAPPDATA%\BetStats\secrets\api-football.json` when no
nonempty environment key is present and the actual host environment is Development.
The file is outside Git, contains plaintext, and must be restricted to the operator
account. Do not copy it into the repository. Its exact format is:

```json
{"ApiKey":""}
```

Paste the actual key between the empty quotes locally and save. No additional
properties are accepted. The reader rejects malformed/duplicate/oversize JSON,
invalid keys, repository paths and links without logging file contents. Missing
or invalid files provide no credential. File rotation is read on subsequent
requests, without modifying historical evidence. Production ignores the file.
Delete this file after temporary use to remove its stored key; removing only the
environment variable does not remove this fallback. Database disable remains the
immediate control across workers. Neither this file nor saving its key activates
transport, grants permission or issues a request.

## Immutable activation grant

Prepare `C:\approved-inputs\provider-grant.json` locally, outside Git. Replace
placeholders with independently reviewed operator evidence and actual source/policy
UUIDs. All booleans below deliberately deny activation until approved by the reviewer.
Do not change real policy obligations to fit these booleans.

```json
{
  "Version": 1,
  "Id": "<new grant UUID>",
  "SourceId": "<existing enabled real source UUID>",
  "SourceCode": "<exact source code>",
  "PolicyId": "<approved source policy UUID>",
  "PolicyVersion": 1,
  "Canary": { "Version": 1, "League": 0, "Season": 0, "From": "<approved yyyy-MM-dd>", "To": "<approved yyyy-MM-dd>" },
  "Category": "PrematchMetadata",
  "PermissionEvidence": "<reviewed operator-specific permission document reference>",
  "EntitlementEvidence": "<reviewed account/subscription league-season entitlement reference>",
  "Reviewer": "<responsible rights and entitlement reviewer>",
  "ExpiresUtc": "<explicit UTC expiry, microsecond precision>",
  "MaximumRequests": 1,
  "MinimumIntervalSeconds": 60,
  "TimeoutSeconds": 30,
  "DurableRetentionApproved": false,
  "NoAttributionRequired": false,
  "EvidenceKind": "OperatorDeclaredReal",
  "FixturePayloadApproved": false
}
```

The source code must be exactly `api-football` or begin with `api-football-`.
The stock network transport rejects Synthetic/Unknown grants, even with a key and
enable flag. OperatorDeclaredReal cannot reuse explicitly synthetic source-policy
or permission/entitlement evidence. Controlled synthetic transport is a test-only
code injection seam; there is no configuration switch to enable it in the Worker.

The reviewer must approve storage of **all original fixtures-response fields**,
including fields ignored by normalization, and derived fixture identities,
competition/season, teams, kickoff/status and regulation-time results. Partial
payload rights are unsupported. `FixturePayloadApproved=true` attests that full
category review; its absence denies activation. Exact acquisition category must
be PrematchMetadata, PostmatchResults or HistoricalResults. This grants no training,
commercial or redistribution rights. The existing policy must independently allow
DataRetrieval, RawPayloadStorage, HistoricalRetention and InternalAnalytics;
PublicDisplay is additionally required by monitoring. Unknown permissions deny.

Finite retention and attribution obligations are unsupported and fail closed:
there is no approved purge/attribution fulfillment mechanism for durable derived
observations. Evidence references are operator attestations, not automated legal
or account certification. The reviewer must actually inspect permission and account
entitlement evidence before using the explicit approval command.

Bounds: total source canary request ceiling 1–100; minimum spacing 1–86400 seconds;
timeout 1–30 seconds. Choose stricter limits than the account's documented daily,
minute and remaining quota. This lifetime source ceiling is intentionally stricter
than a rolling provider quota and cannot reset at midnight/restart/new grant. A
new reviewed grant may explicitly change the total ceiling, never refund usage.
Provider-side remaining quota is unknown; it is not inferred from this ceiling.

See the [activation checklist](provider-activation-checklist.md), executable
[EN](../../runbooks/provider-canary.en.md) / [PL](../../runbooks/provider-canary.pl.md)
runbooks and [quota/recovery/security](../../runbooks/provider-canary-recovery-security.md).
