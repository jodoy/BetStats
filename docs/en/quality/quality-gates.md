# Quality Gates

## Hard failures
- Temporal leakage.
- Target leakage.
- SourcePolicy violation.
- Critical authorization/security regression.
- Invalid model artifact integrity.
- Broken database migration.

## Model promotion
Walk-forward → Brier/LogLoss → calibration → stability → leakage checks → Champion comparison → optional shadow → promote/reject.

## AI changes
Schema validation → golden expected facts → hallucination/grounding/injection metrics → cost/latency → optional judge → human sample.
