# Comparison

Comparisons are directly compatible for correctness when the challenge fingerprint matches. Pairwise target comparisons are classified as:

- `model-only`;
- `agent-system`;
- `hardware`;
- `mixed`.

Reports display objective fields side by side and do not invent an overall weighted winner.

Compatibility fingerprints use inputs whose declarations have been checked against pack contents, plus the resolved validator image identity. A mismatched input is rejected before producing a run. Target snapshots preserve model, agent and provider metadata independently from the challenge fingerprint. Reports keep target failures, assertion failures and validation infrastructure failures visible alongside unweighted coverage.

Validator images are reused locally by verified context content and selected Containerfile, so rebuilding unchanged inputs does not accidentally change compatibility through build metadata. Each run records the resolved immutable image identity in `validator-image.txt`. Removing the local image cache or rebuilding on another Docker host can resolve a different image; differing actual identities remain incompatible.
