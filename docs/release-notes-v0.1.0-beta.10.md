# Codex GPU Thalen Helper v0.1.0-beta.10

This unsigned prerelease closes an Ollama runtime-ownership gap while preserving the existing task-aware routing and model-storage behavior.

## Strict runtime preservation

- Ollama review and CLI validation inspect runtime ownership inside the shared inference lease before any generation.
- An empty runtime may be claimed for the bounded helper operation.
- A loaded runtime is accepted only when exactly one running model matches both the valid helper ownership marker and requested route.
- Untracked CPU-only, GPU-resident, additional, mismatched, and same-name models fail with `FOREIGN_MODEL_LOADED` and are never unloaded.
- Stale, malformed, or otherwise unprovable ownership fails closed.
- Pause, Disable, Release GPU, uninstall cleanup, and validation cleanup unload only a valid tracked helper-owned model; the saved model selection is never used as a fallback ownership claim.

## Verification

- Mocked tests cover different-name CPU and GPU models, same-name ambiguity, stale markers, a valid tracked model, and all three cleanup controls without running local inference.
- The normal isolated test and release-audit pipelines remain required before packaging or publication.
- The installer remains unsigned and can show a Windows SmartScreen warning.
