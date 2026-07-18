# Codex GPU Thalen Helper v0.1.0-beta.7

This unsigned prerelease adds an optional LM Studio provider without weakening the helper's existing Ollama security boundary.

## Highlights

- Registers only audited GGUF files after a complete SHA-256 check and explicit user-authorized validation.
- Adds Qwythos 9B BF16 as a measured 64K standard/deep review route on suitable 24 GB GPUs.
- Keeps quick and GPU-busy work on smaller validated Ollama models and falls back to Ollama when LM Studio or the registered model file is unavailable.
- Verifies the current-user signed LM Studio process on `127.0.0.1:1234` before sending HTTP bytes.
- Shares the existing cross-process one-review GPU lease across providers.
- Refuses to replace or unload user-owned runtime instances.
- Uses LM Studio's native reasoning-off request and verifies the helper-created instance is unloaded after every call.
- Keeps provider identity in routing, validation, recovery state, and MCP results.

No model is downloaded or loaded during installation. SmartScreen may warn because this prerelease is not Authenticode-signed; verify the published SHA-256 checksum before running it.
