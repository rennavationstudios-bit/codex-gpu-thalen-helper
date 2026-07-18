# Codex GPU Thalen Helper v0.1.0-beta.12

Codex GPU Thalen Helper is an independent community project for 64-bit Windows. It is not made, endorsed, or supported by OpenAI, Ollama, or LM Studio.

This prerelease makes automatic local-model routing and lifecycle cleanup fail closed whenever the helper cannot prove that the exact current model and runtime are helper-owned.

## Safety improvements

- Ollama reviews and validation use `keep_alive=0s` and observe release. Cleanup, pause, disable, release, and uninstall do not issue name-based unload or deletion requests, so foreign, replaced, stale, untracked, and same-name ambiguous models are preserved.
- Setup, repair, move, activation, and recovery no longer stop a shared Ollama process when changing `OLLAMA_MODELS`; they require the provider to be closed before rebinding.
- LM Studio registration and routing are temporarily disabled because the current loopback inventory does not expose the absolute file behind a loaded model key. A registration attempt removes stale approval and returns `LMSTUDIO_EXACT_FILE_BINDING_UNAVAILABLE` without running inference.
- Helper-managed MCP configuration accepts only the exact product environment allowlist. Unknown environment entries are preserved and automatic repair is refused.
- Repair precomputes its exact startup/environment write set, never starts or stops Ollama, restores only values it actually wrote, and preserves concurrent edits.
- The dark first-run setup and Control Center now use rounded, DPI-aware action buttons while retaining hover help, keyboard focus, and accessible push-button semantics.
- `INSTALL-WITH-CODEX.md` and the friend bundle's root `0 - PASTE THIS INTO CODEX.md` provide a copy-ready beginner path for Codex to fetch the exact official release, verify provenance, run the installer with consent, and complete the packaged handoff checklist.

## Behavior and boundaries

- No model weights are bundled or downloaded automatically. Automatic routing uses only installed, validated, identity-matching Ollama models that fit current hardware and pressure limits.
- Uninstall always preserves model data. The legacy `--remove-owned-model` switch remains accepted for compatibility but does not delete a mutable Ollama tag.
- Ollama remains loopback-only. Optional automatic startup is per-user and avoids duplicate processes. Declining startup leaves the helper installed, but Ollama must then be started manually before local review.
- The installer surgically merges only helper-managed Codex settings, preserves unknown configuration and instructions, creates timestamped backups, previews protected-file changes, and supports idempotent repair and rollback.
- The MCP server is a bounded read-only advisory reviewer. It exposes no filesystem, shell, Git, deployment, email, or arbitrary mutation tool. Codex remains the primary agent and independently verifies model advice.
- Physical GPU fit, power, thermals, and model performance vary by computer and cannot be certified by the installer.

## Verification and signing

The release is rebuilt from the protected version tag with locked dependencies, isolated temporary Codex homes, mocked provider APIs, the repository test suite, disposable installer lifecycle coverage, privacy checks, package validation, and an SPDX SBOM. GitHub publishes checksums and artifact attestations with the release.

This x64 installer is not Authenticode-signed and remains a prerelease. Verify `SHA256SUMS.txt` and the GitHub artifact attestation before installation. Windows SmartScreen may display an unknown-publisher warning.
