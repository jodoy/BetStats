# ADR 0027: Explicit local football history and transport provenance

Status: Accepted

BS-013 adds an opt-in, versioned historical CSV profile. Local file access is data
retrieval and requires current source authorization before opening the file.
Original bytes and explicit competition/season context are captured before parsing.
The profile is stored on the RAW reference; existing metadata/results profiles and
published artifacts retain their existing interpretation and bytes.

Historical imports use current retrieval and database recording clocks. Publication
time stays null unless explicitly supplied by the source. A date is not a kickoff
instant. Historical labels do not establish pre-event feature availability.

All five provider identities, including the event, must be reviewed for this profile.
No new event is silently selected or created. Existing quality and reconciliation,
append-only corrections, source locks and policy checks remain authoritative.
Imports never certify inventory completeness. Metadata and result coverage remain
separate and require their existing independently reviewed evidence.

API pagination is an abstract bounded protocol only. No HTTP implementation or
credentials are supplied without provider authorization and configuration.
Operator commands are Development-only and never run on host startup.

An additive operation journal stores import fingerprints and fencing owners. A
source-scoped advisory session lock protects each entire bounded import; recovery
must acquire that same lock and an expired owner lease before taking over. RAW
references, identity reviews and result observations retain their existing schema.
Finalized datasets and evaluation/model contracts are unchanged.
