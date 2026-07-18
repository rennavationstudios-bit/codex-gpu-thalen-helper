# Codex GPU Thalen Helper v0.1.0-beta.14

This prerelease adds a reviewable recovery path for a protected-file ownership contradiction discovered during a real beta.11-to-beta.13 upgrade.

## Fixed

- A prior helper installation can now be reconciled when product state still records managed ownership but an external rewrite removed both markers and left exactly one structurally valid unmarked `local_gpu_reviewer` family.
- Ordinary repair continues to refuse that contradiction. Recovery requires an explicit `--migrate-existing` choice, a private read-only dry-run diff, and all four source/planned SHA-256 values at apply time.
- The migration preserves unrelated TOML, comments, unknown settings, MCP servers, and existing instructions; creates timestamped backups; writes atomically; and is idempotent.
- Missing, duplicate, displaced, interleaved, or malformed reviewer families remain blocked. The helper never repairs ownership by silently editing state or inserting markers.

All beta.13 hardware-aware model selection, storage/free-space checks, Ollama acquisition, exact-file LM Studio/Qwythos routing, loopback enforcement, foreign-model preservation, single-flight inference, pressure refusal, and exact-instance unload behavior are unchanged.

This installer is unsigned and remains a prerelease. Verify its SHA-256 checksum and GitHub artifact attestation before installation; Windows SmartScreen may display an unknown-publisher warning.
