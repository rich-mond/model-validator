# Challenge Authoring

A challenge pack supplies:

- `challenge.json`;
- `prompt.md`;
- `workspace/starter.bundle`;
- validator image context or digest-pinned image;
- oracle patch;
- counterexamples and expected failed assertions.

The framework verifies the prompt digest, bundle digest, base commit and validator context digest before execution. Validators are declared as executable argument arrays and run after target execution has ended.

## Repository Boundary

Challenge source belongs in the challenge repository, not in the framework repository. The framework sees the starter only as a Git bundle and the candidate only as files in a workspace.

## Validator Boundary

The validator image contains the private checks. It can use any language/runtime needed by the challenge. The framework only runs the declared assertion commands and records their exit statuses, logs and durations.

## Oracle And Counterexamples

The oracle patch and counterexamples are for challenge-pack verification. They prove that:

- the starter fails at least one required assertion;
- the oracle passes all assertions;
- known-invalid candidates fail the expected assertions.

They are not mounted into target-agent workspaces and are not used to compute candidate correctness during a benchmark run.
