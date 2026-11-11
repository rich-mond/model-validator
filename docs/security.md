# Security

Target code and candidate code are untrusted. Authoritative validation runs in containers with network disabled, capabilities dropped and no-new-privileges enabled.

The framework removes Git remotes from materialised workspaces and never pushes candidate changes.

Local execution still requires a disposable isolated host or VM for strong security.

Container execution controls are a boundary, not a proof of arbitrary hostile-code isolation. Process and interactive targets share the host and remain non-authoritative; absence of an explicit mount is not host filesystem isolation. Treat their evidence accordingly.

Challenge supervisors must compare candidate results outside the candidate process. Successful process exit or a printed success marker cannot establish completion of trusted comparisons. Workers and supervisors still share a validator container; a malicious candidate that tampers with its filesystem or fabricates protocol data is outside the early-termination protection. Use a disposable host for untrusted workloads.
