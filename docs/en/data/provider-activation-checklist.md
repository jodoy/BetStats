# Provider activation checklist

1. Identify the actual account holder, provider and existing source UUID/code.
2. Verify written operator-specific permission, terms version and permitted uses.
   Published documentation and possession of a key are insufficient.
3. Verify actual account entitlement to the exact league/season and fixture dates.
4. Approve all original fixtures response categories and the supported derived
   identities, schedule/status and regulation-time results. Review RAW, durable
   retention, analytics and display separately. Do not infer model-training rights.
5. Resolve retention and attribution requirements; unsupported obligations block.
6. Persist and approve the existing SourcePolicy with its actual evidence. Pin its
   UUID/version in the grant. Resolve overlapping/revoked/expired approvals.
7. Choose one at-most-seven-day UTC canary window, one acquisition purpose/category,
   expiry, total source request ceiling, minimum spacing and timeout compatible
   with actual account quotas. Allow failures/uncertainty within the ceiling.
8. Supply the operator key privately via the local environment. Use Development.
9. Register the immutable grant with explicit actor/reason/approval. It is disabled.
10. Inspect `provider-status` and `provider-plan`; confirm scope, purposes, current
    policy, credential presence, remaining local budget and unresolved blockers.
11. Obtain explicit approval for this bounded real canary, then `provider-activate`.
    Plan/enable exactly one BS-015 job and deliberately dispatch `run-once`.
12. Inspect RAW hash/clocks and request evidence. Review all five canonical bindings.
    Explicitly enable/dispatch the prepared BS-019A publication job; verify receipt.
13. Use existing validation, prospective cohort, frozen prediction and evaluation
    jobs. Missing history/coverage/warm-up produces Blocked/Ineligible, not invented
    probabilities or metrics. Existing 43200-second horizon is only a scheduling
    target; actual evidence cutoff and frozen-before-kickoff checks remain decisive.
14. Disable the provider after the bounded canary; verify durable budget usage and
    absence of extra requests. Keep the local reproducible report outside Git.

Implementation session status: **LIVE_CANARY_BLOCKED**. Missing actual operator
permission/entitlement evidence, approved source policy and exact real scope/quota,
operator credentials, explicit enable/activation and approval to execute a real
canary. No real RAW, canonical publication or quota evidence exists from this session.
