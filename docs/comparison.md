# Comparison

Comparisons are directly compatible for correctness when the challenge fingerprint matches. Pairwise target comparisons are classified as:

- `model-only`;
- `agent-system`;
- `hardware`;
- `mixed`.

Reports display objective fields side by side and do not invent an overall weighted winner.

Compatibility fingerprints use inputs whose declarations have been checked against pack contents, plus the resolved validator image identity. A mismatched input is rejected before producing a run. Target snapshots preserve model, agent and provider metadata independently from the challenge fingerprint. Reports keep target failures, assertion failures and validation infrastructure failures visible alongside unweighted coverage.
