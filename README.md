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
$root = Join-Path $HOME "model-validator-work"
New-Item -ItemType Directory -Force -Path $root | Out-Null
Set-Location $root
git clone https://github.com/rich-mond/model-validator.git
git clone https://github.com/rich-mond/model-validator-challenges.git
Set-Location .\model-validator
dotnet restore ModelValidator.slnx --locked-mode
dotnet build ModelValidator.slnx -c Release
dotnet test ModelValidator.slnx -c Release --no-build
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- challenge verify --path ..\model-validator-challenges\dotnet\idempotent-processing
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- challenge verify --path ..\model-validator-challenges\python\order-normalization
```

The verification commands prove that each challenge pack is coherent: the starter workspace fails required checks, the oracle patch passes, the oracle is stable and known-invalid patches fail as expected.

Successful verification prints the checks it performed and a short summary: manifest and digest checks, validator calibration, oracle stability and counterexample count.

The current public challenge catalog contains:

| Pack | Language | Task |
| --- | --- | --- |
| `dotnet/idempotent-processing` | C# / .NET | Make command processing idempotent under duplicate and concurrent delivery |
| `python/order-normalization` | Python | Normalize inbound order events without mutating input |

## Run A Model

For normal use, pick a challenge and run `benchmark`. The default mode is interactive: Model Validator prepares the candidate workspace and prompt, you run the model or agent from your preferred UI, then Model Validator resumes validation.

Example opening the workspace in VS Code:

```powershell
Set-Location .\model-validator
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge ..\model-validator-challenges\python\order-normalization --open vscode --output ..\model-validator-runs\order-normalization-vscode
```

When VS Code opens, choose the model from the extension or UI you normally use and run it against the prepared workspace. The workspace includes `AGENTS.md` and `MODEL_VALIDATOR_TASK.md`, so the model can read the task without you copying text from another location. `AGENTS.md` also tells the model to run available workspace tests or checks before stopping. Return to the terminal and press Enter only after the model has finished.

The CLI then asks what agent/tool and model were actually used. For example, if Copilot Chat reports `Raptor mini`, record the agent as `vscode-copilot`, provider as `github-copilot` and model as `raptor mini`. Those values are stored in the generated target JSON and the attempt's `adapter-output/interactive-metadata.json`.

The command prints the generated target/plan paths, the candidate workspace, the task file path and clear next steps. It should not validate before you press Enter in interactive mode. If VS Code cannot be opened automatically, the command prints the workspace and task file paths so you can open them manually.

You can also run without opening an editor:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge ..\model-validator-challenges\python\order-normalization --output ..\model-validator-runs\order-normalization-manual
```

The command prints the workspace and task file paths. Use any model or coding-agent UI to edit the workspace, then press Enter to score the result.

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

For unattended automation, pass the agent command after `--`. Use `{prompt}`, `{promptPath}`, `{workspace}`, `{output}` and `{model}` as placeholders:

```powershell
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge ..\model-validator-challenges\dotnet\idempotent-processing --agent codex-cli --provider openai --model gpt-5 --output ..\model-validator-runs\codex-cli-run -- codex exec --model {model} --sandbox workspace-write --ask-for-approval never {prompt}
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
  "challengePath": "<challenge-repo>\\python\\order-normalization",
  "targets": [
    "<targets>\\codex-gpt-5-default.json"
  ],
  "attemptsPerTarget": 1,
  "outputPath": "<runs>\\first-order-normalization-run",
  "execution": {
    "maximumParallelTargets": 1,
    "retainWorkspaces": false
  }
}
```

Validate and run it:

```powershell
$planPath = Join-Path $HOME "model-validator-plans\first-order-normalization-run.json"
dotnet run --project .\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- plan validate --path $planPath
dotnet run --project .\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- run --plan $planPath
```

Outputs are written under the plan `outputPath`. Keep those outputs out of both repos.

To print the console score summary again later:

```powershell
dotnet run --project .\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- results --run ..\model-validator-runs\order-normalization-vscode
```

To print the persisted Markdown or JSON reports instead, use `report --run <run-directory> --format markdown` or `report --run <run-directory> --format json`.

## Commands

The CLI is currently run through `dotnet run` from source. The documentation uses `modelval` as the intended command name for a future packaged tool.

```text
challenge verify --path <challenge-directory>
plan validate --path <benchmark-plan.json>
run --plan <benchmark-plan.json>
report --run <run-directory> --format json
report --run <run-directory> --format markdown
results --run <run-directory>
compare --runs <run-directory> <run-directory>
doctor
```

Example from source:

```powershell
dotnet run --project .\src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- doctor
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
