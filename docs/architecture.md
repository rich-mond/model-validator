# Architecture

Model Validator is split into CLI, core domain, execution and reporting projects.

- `ModelValidator.Core` owns immutable contracts, validation, hashing, fingerprints, metrics and comparison classification.
- `ModelValidator.Execution` owns Git, process, adapter, container and validator execution primitives.
- `ModelValidator.Reporting` renders Markdown from authoritative JSON result objects.
- `ModelValidator.Cli` performs argument parsing and orchestration only.

Challenge workspaces are opaque directories materialised from Git bundles. The framework never infers a target language from filenames and never runs language-specific build or test commands directly.
