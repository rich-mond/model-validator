# Running a Benchmark

This document explains how a real benchmark run works across the framework repository and a separate challenge repository.

## Quick Run

Most users should start with `benchmark` in interactive mode.

```powershell
Set-Location .\model-validator
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge ..\model-validator-challenges\python\order-normalization --open vscode --output ..\model-validator-runs\order-normalization-vscode
```

That single command:

- verifies and materialises the challenge;
- generates the target configuration and benchmark plan under `<output>\_generated`;
- writes `AGENTS.md` and `MODEL_VALIDATOR_TASK.md` into the candidate workspace;
- opens or prints the candidate workspace and task file;
- lets you run the model or coding agent from VS Code or another UI;
- waits until you press Enter in the terminal;
- captures the candidate patch;
- runs hidden validator assertions;
- writes `comparison.md`, `comparison.json` and per-attempt evidence;
- prints a console-friendly score summary.

In interactive mode the command pauses after preparing the attempt. It prints the workspace path and task file path, then waits for you to press Enter. While it is waiting:

1. Use the opened VS Code window, or open the printed workspace manually.
2. Choose the model in your VS Code extension or coding-agent UI.
3. Ask the model to follow `AGENTS.md` or `MODEL_VALIDATOR_TASK.md`.
4. Let it run the workspace's available tests or checks when they exist.
5. Return to the terminal and press Enter to validate the result.
6. Record the actual model shown by the UI.

If `--open vscode` cannot find the VS Code launcher, the benchmark does not crash. It prints the workspace and task file paths so you can open them manually.

Automated CLI mode:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge ..\model-validator-challenges\dotnet\idempotent-processing --agent codex-cli --provider openai --model gpt-5 --output ..\model-validator-runs\codex-cli-idempotency -- codex exec --model {model} --sandbox workspace-write --ask-for-approval never {prompt}
```

Supported placeholders after `--` are `{prompt}`, `{promptPath}`, `{workspace}`, `{output}` and `{model}`.

## Explicit Inputs

For advanced scripted runs, the lower-level `run --plan` command takes three inputs:

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

The framework does not talk to model APIs directly. In interactive mode, you choose the model in VS Code or another UI and edit the prepared workspace. In automated mode, the built-in local-command adapter starts the command you supplied.

For example, this automated command runs Codex CLI:

```text
codex exec --model <model> --sandbox workspace-write --ask-for-approval never <prompt>
```

The same pattern works for any coding system whose CLI can edit the current working directory.

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

For interactive runs, `AGENTS.md` and `MODEL_VALIDATOR_TASK.md` are copied into the materialised workspace so editor-based agents can consume the task naturally. The framework adds both files to `.git/info/exclude` before candidate capture, so they are local run instructions rather than part of the candidate answer.

Interactive runs cannot reliably inspect VS Code or Copilot internals. After the model finishes, the CLI asks you to record the model shown by the UI. The generated target JSON and `adapter-output/interactive-metadata.json` are updated before validation evidence is written.

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

To print the console score summary again later:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- results --run ..\model-validator-runs\order-normalization-vscode
```

To print the persisted Markdown or JSON reports instead:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- report --run ..\model-validator-runs\order-normalization-vscode --format markdown
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- report --run ..\model-validator-runs\order-normalization-vscode --format json
```
