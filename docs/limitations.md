# Limitations

Version 1 supports one challenge per benchmark plan and Git bundle materialisation.

Process adapters are non-authoritative because host filesystem and network isolation cannot be guaranteed.

The framework launches target adapters and does not include model-provider clients.

Execution is sequential: `maximumParallelTargets` values other than 1 are rejected. Output directories cannot be reused for resume or overwrite. Missing usage data and unavailable model versions remain explicitly unknown.

Validator container timeouts and Docker launch failures do not pass assertions. Interruption can leave partial evidence without a comparison report; it is not a successful run. The runner does not prove absence of every wall-clock API access or protect a supervisor against arbitrary same-container interference.
