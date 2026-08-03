# Challenge Authoring

The full challenge-pack file-format contract lives with the challenge catalog:

```text
https://github.com/rich-mond/model-validator-challenges/blob/develop/docs/challenge-authoring.md
```

This framework document records the runner-side boundaries that authoring must preserve.

## Pack Inputs

A challenge pack supplies:

- `challenge.json`;
- `prompt.md`;
- `workspace/starter.bundle`;
- validator image context or digest-pinned image;
- visible self-check commands;
- oracle patch;
- counterexamples and expected failed assertions.

The framework verifies the prompt digest, bundle digest, base commit and validator context digest before execution. Validators and self-checks are declared as executable argument arrays, never shell command strings. Self-checks are copied into the generated workspace task as exact required commands. Target agents must run those commands after editing and must not substitute other checks.

## Repository Boundary

Challenge source belongs in the challenge repository, not in the framework repository. The framework sees the starter only as a Git bundle and the candidate only as files in a workspace.

## Validator Boundary

The validator image contains the private checks. It can use any language/runtime needed by the challenge. The framework only runs the declared assertion commands and records their exit statuses, logs and durations.

Self-checks are public sanity checks for the target agent. They do not determine benchmark correctness and must not reveal hidden validator logic, oracle patches or known-invalid examples.

## Oracle And Counterexamples

The oracle patch and counterexamples are for challenge-pack verification. They prove that:

- the starter fails at least one required assertion;
- the oracle passes all assertions;
- known-invalid candidates fail the expected assertions.

They are not mounted into target-agent workspaces and are not used to compute candidate correctness during a benchmark run.
