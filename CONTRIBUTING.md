# Contributing

BetStats is currently a controlled source-available project.

## Workflow
1. Start from a GitHub Issue.
2. Create a short-lived branch, e.g. `feat/bs-001-solution-bootstrap`.
3. Keep the change within Issue scope.
4. Add/update tests and documentation.
5. Open a pull request.
6. All required CI checks must pass before merge.
7. Prefer squash merge.

## Commit examples
- `feat: bootstrap solution structure`
- `fix: preserve conflicting provider observation`
- `test: add temporal leakage regression`
- `docs: add provider source policy`
- `ci: add pull request quality gate`

## Architecture
Read `AGENTS.md` and relevant ADRs before implementation.

## Security
Never place secrets, credentials or production datasets in Issues, commits, logs, fixtures or pull requests.
