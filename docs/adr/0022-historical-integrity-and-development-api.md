# ADR 0022: Historical Integrity and Development API

**Status:** Accepted

## Decision

Capture immutable football file scope alongside RAW, before parsing. A trusted
INSERT clock, source/RAW foreign key and append-only guards protect this additive
context. Existing RAW without independently provable publication scope fails
closed; no migration invents original context or changes recording timestamps.
Historical identity decisions still use T or explicit reconstruction R.

Coverage conflicts compare observation facts inside the exact half-open
intersection, using compatible calendar/UTC coordinates. Missing fact positions
cannot prove agreement. Classification and conflict reporting share one function.
New coverage report semantics are versioned; old frozen v1/v2 artifacts retain
their original calculation semantics and bytes.

Lookback windows end at the explicitly declared prediction day's midnight in the
UTC-calendar basis. Their start is exactly that day minus the declared lookback.
Date-only values are calendar evidence, never inferred kickoff timestamps.

Evaluation horizon is minimum lead time before the justified event boundary.
Kickoff policy requires precise, source-bound event-time evidence with temporal
availability and identity evidence. Calendar policy uses explicit UTC-calendar
midnight. Missing evidence produces structured ineligibility reasons.

Precise time RAW uses a versioned envelope identifying the provider event and
original ingestion RAW. Validate that binding and historical resolved identity.
Unbound legacy/operator clock claims cannot establish source-verified kickoff.
Corrections remain append-only and DateOnly provenance checks remain intact.

Verification response v2 separates artifact integrity, frozen metadata completeness,
current-use authorization and frozen feature reproduction from nullable RAW
availability/hash results. Standard verification does not claim RAW inspection.
Optional deep verification checks current permissions and retention before reading
bytes, then length and SHA-256. Missing RAW does not invalidate immutable artifacts.

Use one OpenAPI generator with a development-only Swagger UI. Production exposes
neither UI nor documentation. Preserve liveness and add only public-safe read
contracts. No canonical/provider-derived data is anonymously disclosed without
PublicDisplay and retention authorization; unsupported surfaces are omitted.
No administrative mutations, RAW bodies, audit records or startup migrations.

## Consequences

Preserve published migrations and existing finalized artifact bytes/hashes.
Add real PostgreSQL regression tests and ASP.NET Core host tests. Runtime database
roles must remain non-owner; administrators can bypass database protections.
No authentication infrastructure, live providers, models or metric execution.
