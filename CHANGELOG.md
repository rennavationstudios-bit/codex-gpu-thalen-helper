# Changelog

All notable changes are documented here.

## 0.1.0-beta.1 - 2026-07-13

- Initial independent community beta for Windows x64.
- Added dynamic hardware/storage detection and versioned model catalog.
- Added bounded local stdio MCP health/review tools using ordinary Ollama generation.
- Added safe Codex TOML and AGENTS override merging with backup/rollback.
- Added per-user idempotent Ollama startup, trusted executable-path process handling, exact model path/tag/digest verification, and fail-closed loopback enforcement.
- Added Control Center, CLI, pause/resume/release, repair/update/model move/uninstall operations.
- Added self-contained packaging, Inno Setup source, tests, CI/security/release workflows, checksums, SBOM, and attestation support.
- Added protected-tag/environment release gating, bounded downloads, race-safe model moves, and byte-preserving malformed-file uninstall recovery.

The beta installer is not Authenticode-signed and may trigger SmartScreen.
