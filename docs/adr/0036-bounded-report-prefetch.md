# ADR 0036: Bounded report prefetch

Status: Accepted for BS-019E implementation; correctness verification required.

Report reads may prefetch immutable evidence in bounded batches. Dictionaries must
be local to one call, preserve complete composite keys and retain zero/one/multiple
match semantics. Current source and policy authorization must still run at each
existing authorization boundary. No DbContext operations may run concurrently.

Do not cache successful authorization across reads. Do not change temporal
predicates, classification priority, limits, pagination or RAW SHA-256 validation.
Missing evidence must continue to fail closed. Avoid new indexes until PostgreSQL
EXPLAIN evidence demonstrates a benefit.
