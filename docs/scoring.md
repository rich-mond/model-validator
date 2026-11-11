# Scoring

Results are objective and unweighted.

Each assertion is `passed`, `failed`, `timed-out` or `infrastructure-error`. Requirement and regression coverage are reported separately. A task is resolved only when target execution completes successfully and every required requirement and regression assertion passes. An empty validation result is unresolved.

No human judgement, LLM judge, source-similarity score or oracle-patch comparison contributes to computed results.

The JSON assertion status uses the existing enum representation: 0 = Passed, 1 = Failed, 2 = TimedOut, 3 = InfrastructureError. No schema version changes are required. Docker exit codes 125/126/127 identify validation infrastructure failures. Incomplete or interrupted validation does not create a completed successful result.

`terminationReason` distinguishes `completed`, `target-failed`, `target-timeout`, `assertion-failed`, `validator-timeout` and `validation-infrastructure-error`. Target failure takes precedence when target and validation both fail; per-assertion statuses preserve the validation outcome. Only Passed contributes to coverage. Validation infrastructure errors are not ordinary incorrect solutions.
