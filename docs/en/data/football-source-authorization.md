# BS-017 source authorization checklist

Use this checklist before any real file is opened. Technical access, a downloaded
file, a source name and internal SourcePolicy approval do not themselves confer
legal rights. Do not use synthetic setup helpers to approve a real provider.

1. Identify the actual source and its enabled DataSource UUID/code. Record the
   responsible operator, rights reviewer, terms/version, evidence references,
   permitted competition/season, categories and original formats independently.
2. Review DataRetrieval, RawPayloadStorage, HistoricalRetention and InternalAnalytics
   separately. Review PublicDisplay for monitoring. Training, redistribution and
   commercial rights remain separate; this workflow grants none implicitly.
3. Create/review the existing SourcePolicy draft and purpose permissions using the
   established [governance process](source-governance.md). Persist the complete
   draft before approval. Resolve overlapping, expired or revoked approvals.
4. Establish retention and deletion responsibilities for source bytes, derived
   observations, immutable artifacts, backups and exported reports. A capped policy
   without a retention plan supported by existing ingestion must be blocked;
   setting a report's intended retention to one day is not a deletion mechanism.
5. Establish attribution obligations. This stage cannot satisfy arbitrary source
   attribution requirements and never asserts AttributionProvided on the operator's
   behalf; such rights fail closed. Do not strip requirements to enable ingestion.
6. Verify the original file format and scope: `football-history-v1`, UTF-8 <=1 MiB,
   up to 500 rows for the acquisition report. Preserve exact original bytes. A
   separately prepared projection needs explicit authorization and provenance.
7. For kickoff evidence obtain the source's actual local date/time, timezone/offset
   or UTC instant and precision. Preserve the exact source-bound claim file. Do not
   convert an unqualified CSV Time column into an invented UTC instant.
8. Confirm reviewed competition, season, teams and event identities in the same
   source. Do not substitute another provider's identifiers or infer fuzzy matches.
9. Use an explicit acquisition plan pinned to policy UUID/version and file hash.
   Inspect the plan before approving a durable job. Changing rights or bytes
   requires a new approved plan, not an automatic retry under expanded rights.
10. For a future real API adapter, additionally document endpoint/schema authority,
    approved capabilities, per-page scope, authentication/configuration ownership,
    budgets, pagination, timeout, Retry-After and retention/attribution handling.
    No real HTTP transport is registered or enabled in BS-017. Credentials and a
    plan alone cannot enable one. Never assume Footiqo/API-Football/Sofascore/
    Flashscore rights or use browser automation/scraping as a fallback.

Missing/unknown permissions, disabled sources, stale policy versions, corrupt RAW,
unsupported scopes/profiles or unsatisfied restrictions block the operation.
OperatorDeclaredReal records a claim. Independent source certification requires
a concrete verified source contract and remains unavailable in this milestone.
