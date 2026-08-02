# Security

Candidate code is untrusted. Authoritative validation should run on a disposable isolated host or VM in addition to container controls. The framework removes Git remotes from materialised workspaces and never pushes candidate changes.

Secrets are read only from explicitly declared environment variable names and are redacted from persisted logs.
