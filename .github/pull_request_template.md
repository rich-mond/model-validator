## Summary

<!-- Explain what changed and why. Keep this human-readable for a reviewer coming in cold. -->

## Repository Boundary

<!-- Confirm this framework repo contains only framework source, schemas, docs, tests, scripts and examples. No challenge source, validator secrets, generated runs or temporary output should be committed here. -->

## GitFlow

- Base branch: `develop`
- Head branch: <!-- feature/... fix/... release/... hotfix/... -->
- This PR does not target `main` directly.

## Validation

<!-- List the commands actually run and their outcomes. Use "not run" with a reason when appropriate. -->

```powershell
dotnet restore ModelValidator.slnx --locked-mode
dotnet build ModelValidator.slnx -c Release
dotnet test ModelValidator.slnx -c Release --no-build
```

## Generated Outputs

<!-- Confirm generated runs, artifacts, logs, verification JSON and temporary workspaces were not committed. -->
