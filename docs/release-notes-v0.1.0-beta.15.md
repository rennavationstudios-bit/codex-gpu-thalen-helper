# Codex GPU Thalen Helper v0.1.0-beta.15

This unsigned prerelease fixes the LM Studio inventory process boundary used by the actual Codex MCP server.

## Fixed

- The fixed, read-only `lms ls --json` and `lms ps --json` child processes now receive a private redirected standard-input stream that is closed immediately after launch.
- The LM Studio CLI can no longer inherit, wait on, or consume the reviewer's MCP JSON-RPC input pipe. This removes the timeout that could exclude an otherwise validated Qwythos model and make automatic standard/deep planning fall back to Ollama.
- Regression coverage launches a child that waits for input and proves the helper supplies EOF without exposing the parent protocol stream.

All beta.14 ownership reconciliation, protected-file backups and hash-bound repair, beta.13 exact-file Qwythos validation, dynamic hardware/resource routing, loopback enforcement, foreign-model preservation, and exact-instance unload behavior remain unchanged.

## Verification expectation

After installation and explicit Qwythos registration, a fresh Codex MCP session should passively report the LM Studio model as eligible. Standard or deep planning may prefer Qwythos when current GPU headroom satisfies the configured reserve; quick or GPU-busy planning should continue choosing the smallest safe validated Ollama model. Planning runs no inference, and both providers must report zero loaded models after validation or review cleanup.

The installer remains unsigned. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
