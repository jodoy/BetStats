# Provider publication: preparation status

BS-019A is not operationally complete. The audit and pure mapping contract are available;
canonical JSON publication, its durable receipts and recovery are not implemented.
Do not enable a JSON profile in the CSV publisher or register a live provider adapter.

Existing BS-015/017 commands continue to operate only their documented workflows.
There are no `provider-publication-plan`, `dispatch`, `verify` or `recover` commands in
this preparation. GET endpoints cannot publish or recover anything.

Before enabling publication, implement and verify current rights/capability/scope checks,
reviewed identity versions, atomic observation/result/receipt commit, parent fencing,
original-RAW recovery and PL/EN receipt monitoring. Explicit operator intent is required
for mutation. A source key or future kickoff is not authorization or prospective evidence.

Recovery design: after RAW capture, verify/read the same manifest; after parsing, reparse
those bytes; during a failed transaction, roll back all canonical writes; after commit but
before acknowledgment, reuse the original receipt. Expired/stale/cancelled owners must be
fenced and attempt exhaustion must stop work. These remain required PostgreSQL tests.

Parsing interprets provider JSON. RAW capture preserves exact bytes and original clocks.
Canonical publication records reviewed observations. Prospective eligibility additionally
requires as-of, rights, coverage, warm-up and cutoff gates. Frozen prediction publication
and post-match evaluation remain separate existing operations.
