# Model Validator

Model Validator is a language-agnostic comparative benchmark framework for coding-agent and model configurations.

It does not contain challenges. It consumes a separate challenge-pack repository, creates an identical clean workspace for each target, runs each target through an adapter, captures the candidate patch, executes hidden validators supplied by the challenge pack, and writes objective JSON and Markdown reports.

## Two Repositories

Model Validator is designed to be used with two repositories:

- Framework repository: this repo. It contains the CLI, contracts, execution engine, reporting, docs, tests and CI.
- Challenge repository: a separate repo such as `model-validator-challenges`. It contains one or more challenge packs: starter bundle, prompt, hidden validator, oracle patch and known-invalid patches.

The framework repo must not contain committed challenge source code, hidden validators, oracle solutions or generated run outputs.

## Real Run Flow

1. Clone this framework repository.
2. Clone a challenge repository beside it.
3. Verify the challenge pack:

   ```text
   modelval challenge verify --path ../model-validator-challenges/dotnet/idempotent-processing
   ```

4. Create target configuration files. Each target describes a complete evaluated system: agent, model, adapter, tools, runtime and limits.
5. Create a benchmark plan that points at one challenge and one or more targets.
6. Run the plan:

   ```text
   modelval run --plan ./benchmark-plan.json
   ```

For each target attempt, the framework clones the same starter Git bundle into a disposable workspace, removes Git remotes, passes only the workspace and prompt to the target adapter, waits for the adapter to finish, captures the final patch, then runs challenge-defined validators in Docker with network disabled.

The target agent never receives the hidden validator, oracle patch or counterexamples.

## Commands

```text
modelval doctor
modelval challenge verify --path <challenge-directory>
modelval plan validate --path <benchmark-plan.json>
modelval run --plan <benchmark-plan.json>
modelval report --run <run-directory> --format json
modelval report --run <run-directory> --format markdown
modelval compare --runs <run-directory> <run-directory>
```

## Documentation

- [Architecture](docs/architecture.md)
- [Running a Benchmark](docs/running-a-benchmark.md)
- [Challenge Authoring](docs/challenge-authoring.md)
- [Adapter Contract](docs/adapter-contract.md)
- [Scoring](docs/scoring.md)
- [Comparison](docs/comparison.md)
- [Security](docs/security.md)
- [Limitations](docs/limitations.md)

## Generated Outputs

Run outputs, audit artifacts, validation evidence and temporary workspaces are intentionally ignored by Git. Keep benchmark results outside commits unless you are deliberately publishing a report artifact somewhere else.
