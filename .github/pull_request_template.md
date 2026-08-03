## Summary

- 
- 
- 

## Repository Boundary

- [ ] This framework repo contains only framework source, schemas, docs, tests, scripts and examples.
- [ ] No challenge source, validator secrets, generated runs or temporary output are committed here.

## GitFlow

- Base branch: `develop`
- Head branch: `feature/...`, `fix/...`, `release/...` or `hotfix/...`
- [ ] This PR does not target `main` directly unless it is an explicit release or hotfix PR.

## Validation

List the exact commands run and their outcomes. Use `not run` with a reason when appropriate.

```powershell
dotnet restore ModelValidator.slnx --locked-mode
dotnet build ModelValidator.slnx -c Release
dotnet test ModelValidator.slnx -c Release --no-build
dotnet run --project src\ModelValidator.Cli\ModelValidator.Cli.csproj -c Release -- benchmark --challenge C:\Work\model-validator-challenges\python\order-normalization --agent codex --model gpt-5 --output C:\Work\model-validator-runs\codex-gpt5-order-normalization
```

## Generated Outputs

- [ ] Generated runs, artifacts, logs, verification JSON and temporary workspaces are not committed.
