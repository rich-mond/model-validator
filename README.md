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

## Run A Model

For normal use, pick a challenge, pick an agent CLI and model, and run `benchmark`.

Example using Codex:

```powershell
cd C:\Work\model-validator
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge C:\Work\model-validator-challenges\python\order-normalization --agent codex --model gpt-5 --output C:\Work\model-validator-runs\codex-gpt5-order-normalization
```

Example using Claude Code:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge C:\Work\model-validator-challenges\python\order-normalization --agent claude --model claude-opus-4-1 --output C:\Work\model-validator-runs\claude-opus-order-normalization
```

The command creates a fresh challenge workspace, runs the selected coding-agent CLI in that workspace, stops the agent, captures the candidate patch, runs the hidden validator and writes the score report.

Outputs are written under the `--output` directory:

```text
<output>/
├── _generated/
│   ├── benchmark-plan.json
│   └── <target>.target.json
├── comparison.json
├── comparison.md
└── targets/
```

The generated JSON is saved so the run can be audited or repeated, but users do not need to write it by hand for the standard path.

The built-in presets are:

| Agent | Command Model Validator Runs |
| --- | --- |
| `codex` | `codex exec --model <model> --sandbox workspace-write --ask-for-approval never <prompt>` |
| `claude` | `claude -p <prompt>` |

If an agent CLI needs a different command shape, pass it after `--`. Use `{prompt}`, `{promptPath}`, `{workspace}`, `{output}` and `{model}` as placeholders:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge C:\Work\model-validator-challenges\dotnet\idempotent-processing --agent custom --provider openai --model gpt-5 --output C:\Work\model-validator-runs\custom-run -- my-agent run --model {model} --prompt-file {promptPath}
```

The target agent receives only the materialised workspace, the prompt and explicitly allowed configuration. It does not receive the validator, oracle patch, counterexamples, Git remotes or repository credentials.

## Advanced Run Shape

The framework still supports explicit target and plan JSON for scripted comparisons. A target is the full system under test, not just a model name.

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

Create a benchmark plan that points at the challenge and target configs:

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
