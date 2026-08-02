# Model Validator

Model Validator is a framework for running coding-agent challenges and comparing complete target configurations.

It answers questions like:

- Did this agent/model/tooling configuration solve the task?
- Which validator assertions passed or failed?
- How long did each attempt take?
- Which metrics were unavailable instead of guessed?
- Are two runs comparable as model-only, or did the agent/runtime/tooling also differ?

It is language-agnostic. The framework is implemented in .NET, but the challenges it runs are opaque workspaces. A challenge can contain .NET, Python, JavaScript, Java, Rust or anything else, as long as it provides a manifest, starter workspace and validator.

## Repository Roles

Model Validator uses two repositories.

| Repository | Purpose | Contains |
| --- | --- | --- |
| `model-validator` | The runner framework | CLI, schemas, execution engine, scoring, reports, docs and tests |
| `model-validator-challenges` | Challenge packs | Prompts, starter bundles, hidden validators, oracle patches and known-invalid patches |

This repository is only the framework. It should not contain committed challenge source, hidden validators, oracle solutions, benchmark outputs, run logs or candidate patches.

## What You Can Do Immediately

From a fresh machine, you can clone both repos, build the framework and verify the supported challenge packs.

```powershell
cd C:\Work
git clone https://github.com/rich-mond/model-validator.git
git clone https://github.com/rich-mond/model-validator-challenges.git
cd C:\Work\model-validator
dotnet restore ModelValidator.slnx --locked-mode
dotnet build ModelValidator.slnx -c Release
dotnet test ModelValidator.slnx -c Release --no-build
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- challenge verify --path C:\Work\model-validator-challenges\dotnet\idempotent-processing
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- challenge verify --path C:\Work\model-validator-challenges\python\order-normalization
```

The verification commands prove that each challenge pack is coherent: the starter workspace fails required checks, the oracle patch passes, the oracle is stable and known-invalid patches fail as expected.

The current public challenge catalog contains:

| Pack | Language | Task |
| --- | --- | --- |
| `dotnet/idempotent-processing` | C# / .NET | Make command processing idempotent under duplicate and concurrent delivery |
| `python/order-normalization` | Python | Normalize inbound order events without mutating input |

## What You Need For A Real Benchmark

A real benchmark needs one thing this repository does not ship with yet: a target adapter.

The framework does not call Codex, Claude Code or another coding agent directly. Instead, it starts an adapter process or container. The adapter is responsible for running the agent against the materialised workspace.

The framework supplies the adapter with:

```text
--workspace <candidate-workspace>
--prompt <prompt-file>
--output <target-output-directory>
```

The adapter should:

1. Read the prompt.
2. Run the chosen coding agent in the supplied workspace.
3. Leave the final candidate files in that workspace.
4. Optionally write usage metrics to `usage.json`.
5. Exit.

After the adapter exits, Model Validator captures the candidate patch and runs the challenge validator. The target agent never receives the hidden validator, oracle patch, known-invalid patches, Git remotes or repository credentials.

## Run Shape

Create one target config per evaluated system. A target is the full system under test, not just a model name.

```json
{
  "schemaVersion": "1.0",
  "targetId": "codex-gpt-5-default",
  "displayName": "Codex GPT-5 default",
  "agent": {
    "name": "codex",
    "version": "record-the-version-used"
  },
  "model": {
    "name": "gpt-5",
    "version": "record-the-version-used",
    "provider": "openai"
  },
  "adapter": {
    "mode": "process",
    "image": null,
    "command": [ "path-to-your-adapter" ],
    "workspacePath": "/workspace",
    "promptPath": "/input/prompt.md",
    "outputPath": "/output"
  },
  "environment": {
    "allowedVariables": [],
    "secretVariables": []
  },
  "limits": {
    "cpuCount": 2,
    "memoryBytes": 2147483648,
    "pids": 128
  }
}
```

Create a benchmark plan that points at the challenge and the target configs:

```json
{
  "schemaVersion": "1.0",
  "planId": "first-order-normalization-run",
  "challengePath": "C:\\Work\\model-validator-challenges\\python\\order-normalization",
  "targets": [
    "C:\\Work\\targets\\codex-gpt-5-default.json"
  ],
  "attemptsPerTarget": 1,
  "outputPath": "C:\\Work\\model-validator-runs\\first-order-normalization-run",
  "execution": {
    "maximumParallelTargets": 1,
    "retainWorkspaces": false
  }
}
```

Validate and run it:

```powershell
dotnet run --project C:\Work\model-validator\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- plan validate --path C:\Work\plans\first-order-normalization-run.json
dotnet run --project C:\Work\model-validator\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- run --plan C:\Work\plans\first-order-normalization-run.json
```

Outputs are written under the plan `outputPath`. Keep those outputs out of both repos.

## Commands

The CLI is currently run through `dotnet run` from source. The documentation uses `modelval` as the intended command name for a future packaged tool.

```text
challenge verify --path <challenge-directory>
plan validate --path <benchmark-plan.json>
run --plan <benchmark-plan.json>
report --run <run-directory> --format json
report --run <run-directory> --format markdown
compare --runs <run-directory> <run-directory>
doctor
```

Example from source:

```powershell
dotnet run --project C:\Work\model-validator\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- doctor
```

## Result Model

Correctness is computed only from validator assertion exit codes.

Model Validator does not use:

- human judgement;
- LLM judges;
- source similarity;
- oracle-patch comparison;
- arbitrary scoring weights.

Elapsed time and correctness are always recorded. Token, request and cost metrics are recorded only when the adapter provides them; unavailable metrics remain `null`.

## Documentation

- [Running a Benchmark](docs/running-a-benchmark.md)
- [Adapter Contract](docs/adapter-contract.md)
- [Challenge Authoring](docs/challenge-authoring.md)
- [Architecture](docs/architecture.md)
- [Scoring](docs/scoring.md)
- [Comparison](docs/comparison.md)
- [Security](docs/security.md)
- [Limitations](docs/limitations.md)
