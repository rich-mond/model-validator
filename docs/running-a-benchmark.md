# Running a Benchmark

This document explains how a real benchmark run works across the framework repository and a separate challenge repository.

## Inputs

A run needs three inputs:

- A challenge pack directory, for example `../model-validator-challenges/dotnet/idempotent-processing`.
- One or more target configuration JSON files.
- A benchmark plan JSON file that references the challenge and targets.

The challenge pack supplies the task. The target configuration supplies the evaluated coding system. The benchmark plan connects them.

## What the Framework Does

For each target attempt, the framework:

1. Verifies the challenge manifest, prompt digest, starter bundle digest and validator context digest.
2. Clones the starter Git bundle into a fresh disposable workspace.
3. Verifies the workspace commit equals the challenge base commit.
4. Removes every Git remote from the workspace.
5. Creates a local disposable branch.
6. Runs the target adapter with the workspace path, prompt path and output path.
7. Waits for the adapter to terminate.
8. Captures the candidate patch and changed-file inventory from the workspace.
9. Runs the challenge-defined hidden validators in Docker.
10. Writes per-attempt JSON, validator logs, candidate patch and comparison reports.

## What the Agent Does

The framework does not talk to model APIs directly. A target adapter does that.

For example, a Codex adapter could:

1. Read the prompt file path supplied by the framework.
2. Start Codex against the supplied workspace.
3. Let Codex edit files in that workspace.
4. Exit when Codex is done or when the timeout is reached.
5. Optionally write usage metrics to `usage.json`.

The same pattern works for another coding system, such as Claude Code or a local model-backed agent. The adapter is the boundary between Model Validator and the evaluated agent.

## What Validation Means

Validation is not a comparison against the oracle patch. Validation is objective assertion execution.

The challenge manifest declares assertions such as:

```json
{
  "id": "duplicate-delivery",
  "classification": "requirement",
  "required": true,
  "command": ["/validator/run", "duplicate-delivery"],
  "workingDirectory": "/candidate",
  "timeoutSeconds": 120
}
```

The framework runs that command inside the challenge validator container with the candidate workspace mounted at the declared path. Exit code `0` means the assertion passed. Non-zero means it failed, unless the framework classifies the event as timeout or infrastructure failure.

## Why Hidden Validators Stay Hidden

During target execution, the adapter receives only:

- the materialised starter workspace;
- the prompt file;
- a standard output directory;
- explicitly allowed environment variables.

It does not receive:

- `challenge.json`;
- validator files;
- oracle patch;
- counterexamples;
- repository credentials;
- Git remotes.

Only after target execution ends does the framework run the validator container.

## Outputs

A run writes to the benchmark plan output directory:

```text
<output>/
├── plan.snapshot.json
├── challenge.snapshot.json
├── comparison.json
├── comparison.md
└── targets/
    └── <target-id>/
        └── attempt-001/
            ├── result.json
            ├── candidate.patch
            ├── changed-files.json
            ├── target.stdout.log
            ├── target.stderr.log
            └── validators/
```

These outputs are generated artifacts and should not be committed back to either repository.
