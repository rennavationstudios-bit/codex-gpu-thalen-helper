# Codex GPU Thalen Helper v0.1.0-beta.8

This unsigned prerelease fixes automatic Ollama startup after Windows sign-in.

## Fix

- An unused Ollama port is now recognized as safe for the helper to start on `127.0.0.1`.
- The startup guard no longer mistakes an absent listener for a network-exposed listener.
- Real wildcard or non-loopback listeners remain blocked before any model or prompt request.
- Startup still uses one cross-process mutex and never creates a duplicate Ollama process.

All beta.7 provider-aware LM Studio routing, model integrity, one-review concurrency, pressure guards, and verified unload behavior remain in place.

No model is downloaded or loaded during installation. SmartScreen may warn because this prerelease is not Authenticode-signed; verify the published SHA-256 checksum before running it.
