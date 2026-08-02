# Security

Target code and candidate code are untrusted. Authoritative validation runs in containers with network disabled, capabilities dropped and no-new-privileges enabled.

The framework removes Git remotes from materialised workspaces and never pushes candidate changes.

Local execution still requires a disposable isolated host or VM for strong security.
