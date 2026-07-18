# Codex GPU Thalen Helper v0.1.0-beta.6

This unsigned Windows x64 prerelease adds a protected, reviewable upgrade path for users who already have an unmarked `local_gpu_reviewer` integration. Default behavior still preserves external integrations without taking control.

## Changes

- Added a read-only repair dry-run that writes the exact before/after diff only to an explicitly selected private local file.
- Added source/planned SHA-256 binding for both `config.toml` and `AGENTS.override.md`; all four values must still match before either protected file is written.
- Added explicit `--migrate-existing` adoption after review. Ambiguous, duplicate, displaced, or interleaved TOML reviewer layouts are refused.
- Preserves the existing AGENTS file as an exact prefix during first managed adoption and appends one sanitized managed local-GPU section.
- Refreshes persisted product version after repair, compares update versions semantically, and returns a failing CLI exit code for failed update results.
- Adds a SHA-pinned disposable beta.5-to-beta.6 package lifecycle test with no model download or inference.
- Automatic routing now requires a privacy-safe per-model validation record for the exact installed digest; failed, missing, corrupt, or stale validation evidence fails closed without recording prompts or responses.

## Safety and limitations

- Existing unmarked reviewers remain user-owned unless migration is explicitly requested with a reviewed hash-bound plan.
- The installer never downloads or loads a model by default. It preserves pre-existing model files, uses only a user-approved fixed local model directory, and does not move or delete unrelated storage.
- Per-user Ollama startup remains an explicit choice. Managed startup checks the loopback endpoint and existing Ollama processes before launching, while a declined choice leaves manual startup required after sign-in.
- Ollama is restricted to loopback. The MCP reviewer remains read-only, exposes only passive health/planning plus bounded review, and receives only text Codex explicitly supplies.
- Dry-run diff files can contain private local configuration or instructions. Keep them outside repositories and synced folders, do not share them, and delete them when no longer needed.
- Automated release tests use temporary Codex homes and mocked/local passive runtime boundaries. They do not reboot a destination PC, run a real GPU model, or prove every driver/model combination.
- Software cannot prove PSU capacity, PCIe power cabling, card clearance, cooling, or every firmware/driver combination. Users must verify those physical limits before adding or changing GPU hardware.
- The installer is not Authenticode-signed. Windows SmartScreen may display an unknown-publisher warning; verify `SHA256SUMS.txt` and the GitHub artifact attestation.
- The exact release checksums are published in the attached `SHA256SUMS.txt`; do not trust an installer whose digest is absent or does not match.
- This is an independent community project and is not made, endorsed, or supported by OpenAI or Ollama.
