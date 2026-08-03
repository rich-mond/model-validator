# Adapter Contract

Most users do not need to write an adapter. Use `modelval benchmark --agent <agent> --model <model>` and Model Validator will run the built-in local-command adapter with the selected agent CLI.

This contract matters when adding a new first-class adapter mode or when the built-in command form is not enough.

Target adapters receive:

- a writable workspace;
- a read-only prompt path;
- a writable output path;
- explicitly allowed environment variables;
- time and resource limits.

Adapters leave the final candidate in the workspace and may write `usage.json` to the output path. Missing usage fields are recorded as null.

Container adapters are authoritative. Process adapters are convenience mode and are marked non-authoritative.

## Process Adapter Shape

A process adapter command is declared as an argument array. The framework appends:

```text
--workspace <workspace-path> --prompt <prompt-path> --output <output-path>
```

The adapter should run the evaluated coding agent against `<workspace-path>` using the contents of `<prompt-path>`.

## Container Adapter Shape

A container adapter is run by Docker. The framework mounts:

- workspace read/write at `adapter.workspacePath`;
- prompt read-only at `adapter.promptPath`;
- output read/write at `adapter.outputPath`.

The framework also applies network/resource/security controls according to the target limits and adapter mode.

## Usage Metrics

Adapters may write `usage.json` into the output directory:

```json
{
  "inputTokens": 1000,
  "cachedInputTokens": 0,
  "outputTokens": 250,
  "requests": 4,
  "providerReportedCost": 0.12,
  "currency": "USD"
}
```

Missing fields are stored as null. Missing or malformed usage files do not affect correctness.
