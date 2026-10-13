# ADR 0032: First provider boundary, with live activation blocked

Status: Accepted for the independently testable boundary only

## Initial audit

Fetched main `ae49fcecdce3e320e02b11e6d9688e3151bfae21`. GitHub confirms
BS-017 PR #21 merged. Reviewed BS-013–017 verification, ADRs 0027–0031,
provider execution/budgets, ingestion/RAW, durable pipeline and read-only dashboard.
There is no equivalent concrete provider client. Existing acquisition dispatch
only understands approved local files; its historical CSV profile must not be
used to label API JSON. Existing provider interfaces permit controlled transport.
Existing operator Web launch settings are excluded from delivery.

No operator credentials, competition entitlement, rights-holder grant or approved
SourcePolicy for a concrete provider were supplied. Published terms are evidence
for review, not an approval. API-Football v3 is the technical boundary candidate;
no provider is selected for live activation. See the provider decision matrix.

## Decision before implementation

Exercise one documented endpoint (`/fixtures`) through an injected HTTP message
invoker. No client, transport, schedule or automatic dispatch is registered.
Reuse AuthorizedProviderExecutor, RequestBudget and source-policy evaluation;
pin the BS-017 AcquisitionPageContext and explicitly bound one league/season and
seven-day window. A separate versioned JSON reader describes provider observations,
never canonical entities. Preserve original bytes as content before interpretation.
The reader rejects ambiguous time/status/result data and duplicate/conflicting IDs.
HTTP errors are classified without response bodies, exception messages or secrets.
Zero implicit retries; existing fenced job attempts remain the future retry boundary.

This is the explicit BS-018 missing-authorization fallback. Do not add a partial
network-to-database acquisition route or pretend an in-memory response is a durable
RAW receipt. Persistent capture, identity review, time/result publication and
schedule activation remain blocked until the provider-specific legal/capability
contract is approved. No second scheduler, new model, schema migration, prediction
gate relaxation or reinterpretation of existing immutable artifacts is introduced.

Show the blocked readiness and exact limitations in PL/EN. Existing BS-017 monitor
continues to show actual stored acquisition/cohort evidence. Do not manufacture
last-success clocks, quota remaining, upcoming matches or evaluation metrics.

## Consequences

Mocked HTTP verification is independent of real provider verification. Configuring
a key alone never activates anything. A follow-up approved integration must wire
original JSON RAW and reviewed mapping into existing durable child operations,
then demonstrate interruption, revocation, corrections and a real prospective
canary before claiming operational readiness. This deliverable cannot run live.
