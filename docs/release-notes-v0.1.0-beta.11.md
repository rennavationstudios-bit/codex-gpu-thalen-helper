# Codex GPU Thalen Helper v0.1.0-beta.11

This prerelease fixes post-restart Ollama routing when Codex launches the helper-owned stdio MCP server with an explicit environment policy.

## Fixed

- The managed MCP block now whitelists `OLLAMA_MODELS` with Codex's supported `env_vars` setting.
- Fresh reviewer processes receive the current user's helper-verified model-store path and still require it to match product state before any Ollama request.
- Existing beta.10 managed blocks are detected as repairable drift and upgraded only through the existing previewed, hash-bound protected-file repair flow.

LM Studio routing, strict Ollama runtime ownership, foreign-model preservation, shared inference locking, pressure guards, and immediate unload behavior are unchanged.

This installer is unsigned and remains a prerelease. Verify its SHA-256 checksum before installation; Windows SmartScreen may display an unknown-publisher warning.
