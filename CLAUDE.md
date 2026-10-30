# CLAUDE.md

@AGENTS.md

The rules in AGENTS.md apply in full. The section below adds focus rules for the current phase.

## Current phase: Vertical slice to a visible product

Goal: real football data for ONE league flows end-to-end into predictions that a user can
see in the browser. Infrastructure is considered sufficient for this phase.

### Focus rules
1. Every Issue must end with something observable: an API response, a UI view or a single
   command that produces a visible result. State it in the PR description with an example.
2. Do NOT add new governance, audit, fencing, retention, canary or operator-safety layers
   unless the Issue explicitly asks for them. If you believe one is needed, stop and write
   it down as a proposal in the PR description instead of implementing it.
3. Do NOT create a new ADR unless the Issue changes architecture in a way existing ADRs do
   not cover. Prefer a short note in the PR description.
4. Reuse existing code paths (BS-013 CSV import, BS-018/019 API-Football adapter,
   BS-012 models, BS-011 backtesting) before writing new ones.
5. Keep diffs small: one Issue, one PR, ideally under ~600 changed lines excluding migrations.

### Code style for new and touched code
- One statement per line; no lines longer than 140 characters.
- Split long boolean validation into named guard clauses or small methods.
- Prefer clear names over comments; add XML docs only to public contracts.

### Definition of done
- `dotnet build` and `dotnet test` pass (see README for the three verification commands).
- The observable result from rule 1 is demonstrated in the PR description.
- README is updated only if user-facing setup or behaviour changed.
