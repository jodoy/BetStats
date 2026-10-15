# ADR 0034: Authorized Provider Activation

Status: Accepted for engineering; live operation requires operator evidence.

## Decision before implementation

Extend BS-015 with a bounded provider acquisition kind. Persist an immutable grant
pinning source, policy, endpoint capability, categories, league/season/date window,
permission and entitlement evidence, reviewer, expiry, retention/attribution and
request budget. An explicit Development operator action enables a grant; a key or
configuration flag cannot approve it. Evidence references are operator attestations,
not independent legal certification. Unknown or unsupported obligations deny.

Serialize reservations on the source row, after job/execution locks. Each reservation
consumes the lifetime source canary budget, including failures and uncertain calls.
One request may be in flight per source. Database clocks enforce spacing and
Retry-After across workers/restarts. Disable prevents new reservations immediately;
an already reserved request is in flight and cannot be recalled. Recheck current
rights before preserving its response. Never refund reservations or retry HTTP
implicitly. An uncertain request fails closed until explicit operator reconciliation.

Use a dedicated shared HttpMessageInvoker/SocketsHttpHandler equivalent to a pooled
client: fixed HTTPS host, no redirect/proxy/cookie, no automatic retries, bounded
timeout/body, no HttpClient logging pipeline exposing headers. Obtain the key from
the operator environment or a Development-only file at
`%LOCALAPPDATA%/BetStats/secrets/api-football.json`. Environment takes precedence;
invalid files deny credentials without logging content. Read the bounded file per
request for rotation, never in Production or from a repository/link. The file can
supply only the key, never enablement or approval. No secret enters persisted plans
or reports. This local plaintext file relies on the operator's filesystem permissions.

RAW capture retains the existing transaction and trusted recording clock. Persist
the ingestion run UUID with the reservation before HTTP. Recovery finds original
RAW by that run, even after capture-before-ack interruption, and never downloads
again. If no RAW exists after a reservation, the response is uncertain: a network
response cannot be transactionally committed with PostgreSQL. Preserve the consumed
budget and require operator reconciliation, rather than claiming exactly-once HTTP.

Acquisition produces a deterministic disabled BS-019A publication job from original
RAW. The operator enables/dispatches it through existing commands; identity review
and reprocessing reuse its original RAW. Prediction/evaluation remain separate
BS-015 jobs with existing chronology, warm-up and completeness gates. Configuration
and synthetic tests never certify a prospective cohort or prediction readiness.

Read-only Development-loopback reports show safe activation/request/RAW/publication
links without secrets, evidence-document contents or provider calls. Historical
RAW, observations, receipts, predictions and evaluation artifacts remain immutable.
