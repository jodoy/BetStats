# ADR 0019: Quality, Identity Review and Reconciliation

**Status:** Accepted

## Decision

Keep the modular monolith, existing parser, RAW store, policy evaluator, identity
ledger and publication receipts. Domain owns provider-neutral quality outcomes;
Application owns a versioned deterministic football rule catalog and operator
ports. Infrastructure provides PostgreSQL adapters. No public administrative API.

Assessments are immutable per execution / RAW / row / rule version. Each rule
records pass or issue, severity, eligibility blocking and a stable reason code.
Severity is independent of eligibility. Classification distinguishes equivalent
duplicates, contradictory duplicates, ambiguity, mismatches, fact conflicts,
historical corrections and invalid transitions. No provider is declared wrong
merely because another disagrees. Date-only facts never imply kickoff times.

Operator decisions append to IdentityResolution with expected-version checking
under a source lock shared with ingestion and policy decisions. Rejected proposals
append Unresolved (no target) and an explicit Reject maintenance action. Identity
sport must be supported by existing canonical or football RAW evidence; missing
evidence fails closed. The supplied operator identifier is not authenticated.

Reconciliation accepts at most 20 explicit RAW/context pairs, verifies SHA-256,
rechecks current rights and source enablement and reuses publication receipts.
Rules are also evaluated inside publication's source transaction before writing
facts. Rows with existing published facts from newer retrieval evidence cannot
restore old values. Mapping changes require explicit reconstruction; reconciliation
does not rewrite an old target or republish older facts onto a changed target.
Every reconciliation has protected Started/terminal maintenance evidence. A
stopped execution may be explicitly marked Interrupted; partial committed work
remains and receipts make a retry safe. No filesystem/database atomicity claim.

HistoricalAsKnown uses decisions, observations, quality and policies known by the
cutoff, including database INSERT recording times. RetrospectiveReconstruction
requires a separate reconstruction time, may interpret the same historical fact
through a later reviewed mapping, and returns both decisions and frozen original
target references. It never backdates observations or silently changes modes.
Present operational status and current usage rights are additional safety gates.
No commit snapshot or materialized dataset is promised. Missing quality evidence,
unsupported rule versions, incomplete provenance and unresolved blockers deny.

Quality reports aggregate exclusive row outcomes, separately report rule issues,
and define denominators explicitly. Queries are bounded with stable ordering and
overflow failure, rather than silently truncated totals. Append-only assessments
and maintenance events receive database clock timestamps and UPDATE/DELETE/
TRUNCATE guards; privileged owners can bypass them. No retention bypass or purge.

## Consequences

Additive migration preserves published migrations and all earlier history.
Synthetic tests use isolated PostgreSQL 17 and private temporary RAW roots.
Worker exposes explicit development operator commands; no scheduler, startup
migration, authentication, HTTP transport, scores, ML or frontend is introduced.
