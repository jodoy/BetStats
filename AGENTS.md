# AGENTS.md — BetStats Engineering Rules

## Scope discipline
1. Implement only the linked Issue acceptance criteria.
2. Do not introduce unrelated refactors or speculative abstractions.
3. Prefer one working vertical slice over broad scaffolding.
4. Architecture-impacting changes require an ADR before implementation.

## Architecture
1. BetStats is a modular monolith.
2. Provider DTOs are not domain entities.
3. Provider IDs must not live directly on canonical entities.
4. Persist permitted RAW input before normalization.
5. Preserve provenance and provider observations; never silently overwrite conflicts.
6. Prediction code consumes canonical data/features, never provider DTOs.
7. Every temporal feature must obey `AsOfUtc`; temporal leakage is a hard failure.
8. `PredictionSnapshot` is immutable.
9. Core prediction is independent of bookmaker odds.
10. Real-money betting functionality is out of scope.

## Data and licensing
- Every source requires `SourcePolicy`.
- Technical access does not imply display, training, redistribution or commercial rights.
- Do not commit proprietary datasets or unclear-license payloads.
- Prefer free sufficiently reliable data; paid data fills proven gaps.

## Quality
- New business logic requires tests.
- Every provider requires contract tests.
- Every bug fix requires a regression test.
- Database changes require migrations.
- ML changes require walk-forward and calibration evidence.
- AI changes require evaluation evidence.
- Never bypass security, licensing, leakage or critical quality gates.

## Language
- Code, symbols, logs, ADRs, Issues, PRs and primary engineering docs: English.
- Product UI: `pl-PL` and `en-GB`.
