# ADR 0033: Durable provider publication

Status: Accepted for publication from already captured, authorized RAW. Live HTTP is out of scope.

## Audit and reuse

See the [pre-implementation audit](../en/quality/bs-019a-audit.md). PR #22 was merged;
BS-018 supplies strict JSON parsing but no durable publisher. The mapper and its original
43 cases remain. Reuse canonical Sport/Competition/Season/Participant/SportingEvent,
ProviderIdentity/IdentityResolution, Observation, EventTimeEvidence, independent football
results, source governance and BS-015 jobs. No parallel domain or scheduler is introduced.

## Atomic boundary and clocks

`ProviderPublication.PublishAsync` consumes a source-bound existing RAW plan. It reads
original bytes from IRawPayloadStore, verifies SHA-256, profile, length, source code/UUID,
original scope, retrieval/recording clocks and the pinned policy at original recording and
current time. It invokes the existing BS-018 mapper. No retrieval or recapture is performed.

Within one READ COMMITTED transaction it locks job, execution, then source. It checks
current owner/attempt/lease/cancellation and resolves current reviewed identity decisions.
It writes all eligible fixture observations, quality evidence, precise kickoff evidence,
independent results and the authoritative immutable receipt atomically. A first save
flushes canonical rows; a second writes the receipt after their database recording clocks.
Both saves roll back together. Rows requiring review or excluded by quality produce
explicit receipt outcomes/exclusions; a partial batch never claims whole-batch success.

`ingestion.ProviderPublications` stores a bounded canonical manifest, hash and unique
(source, key). The key includes RAW identity/hash, full plan including origin, policy,
mapping version and every selected decision UUID/version/target. The manifest binds
canonical IDs, observation IDs, correction links, kickoff, nullable source publication,
original clocks, actual publication time, job/execution/attempt and owner. Database
triggers assign the recording clock and prohibit UPDATE/DELETE/TRUNCATE. A deferred
constraint trigger checks owner, attempt, cancellation and unexpired lease at commit.
After commit an independent context/connection verifies persisted receipts and references;
pending tracked writes are never reported as committed evidence.

## Revocation and concurrency

The existing governance audit trigger locks the same source row before approval/revocation;
existing identity review also locks it. Publication revalidates rights and RAW just before
commit while retaining all locks. A revocation committed first blocks the publisher. A
revocation waiting behind an authorized publication takes effect after that transaction;
it does not retroactively erase committed evidence. Source disable updates also serialize
with this lock. These are database transaction order guarantees, not claims about a legal
request arriving outside the database. Missing/unsatisfied attribution is denied. Bounded
retention is also denied, even before expiry: this release has no approved expiry/purge
lifecycle for durable observations and cannot invent satisfaction of that restriction.
The deferred trigger rechecks effective policy intervals, required purposes and these
restrictions at commit as well as checking the owner fence. No grants are invented.

## Identity and history

All five competition/season/home/away/event bindings must be explicitly reviewed. Unknown
anchors receive unresolved decisions bound to original RAW, allowing existing review tools
to inspect them. No names are used to choose targets; no teams, competitions or events are
automatically invented. Canonical event context, season interval, sport and home/away roles
must agree. Different fixtures mapped to one canonical event in a payload are blocked.
After review, a changed decision context creates a new immutable receipt. A reviewed event
target correction requires newer RAW and creates forward observations; old targets and
frozen artifacts remain unchanged. Reinterpreting old RAW into a different published target
is deliberately blocked. Ambiguity never chooses the first candidate.

Date, ScheduledStart and EventStatus observations retain correction chains and actual
availability/recording clocks. FT regulation-time results are independently versioned;
nullable half-time scores stay nullable. AET/PEN completion does not imply supported final
labels. Unknown statuses are rejected or explicitly excluded, never finalized. A result
observed later cannot be made available at an earlier prediction cutoff. Missing source
publication time remains null; kickoff is not substituted for publication/observation time.

## Recovery and parent acknowledgment

Canonical publication/receipt is stage one. BS-015 artifact/terminal acknowledgment is
stage two and remains a separate transaction. If interruption occurs before stage-one
commit, its writes roll back and recovery reparses the same original RAW. If interruption
occurs after stage-one commit, the same immutable key reuses the receipt without new
observations. Parent acknowledgment rechecks governance/integrity. It may be blocked or
cancelled after a valid child commit; the committed child receipt remains inspectable.
Existing BS-015 live-session locks, bounded attempts, retry/recover and cancellation are
reused. Exhaustion stops execution; recovery never makes a provider request.

## Evidence and monitoring

BS-016 validation and BS-017 acquisition/cohort readers accept linked JSON publication
observations. Existing precise-time/result readers verify JSON against original RAW.
Origin (Synthetic/OperatorDeclaredReal/Unknown), chronology, source timestamps, coverage
and prediction eligibility remain separate. No independent coverage, warm-up history,
prediction or metric is fabricated. Current display rights are checked by bounded
Development-loopback GET projections and PL/EN views. GET performs no mutation or model
execution. There are no public mutation endpoints, startup migrations or HTTP activation.

## Schema and verification

Additive migration `20261014115707_DurableProviderPublication` adds only the receipt table,
indexes, restrictive FKs, trusted recording/immutability and deferred fencing triggers.
Fresh creation, upgrade from the preceding schema and legacy byte/hash/clock invariants
are exercised on disposable PostgreSQL. See the verification report and executable
[English](../runbooks/provider-publication.en.md) / [Polish](../runbooks/provider-publication.pl.md) runbooks.
