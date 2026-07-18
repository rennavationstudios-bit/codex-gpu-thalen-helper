# Codex GPU Thalen Helper v0.1.0-beta.13

This unsigned prerelease restores a fail-closed LM Studio route and makes first-run model setup provider-aware.

## What changed

- Existing LM Studio GGUF models can be registered only when the signed current-user `lms.exe` inventory binds the catalog model key to the exact audited relative path, size, file identity, and full SHA-256 digest.
- Registration and every LM Studio review repeat that proof inside the shared single-review lease. The helper refuses foreign loaded instances, generates through the verified loopback provider, unloads only the exact helper-created instance, and confirms it is absent afterward.
- Legacy registrations without the new proof remain preserved but cannot route until the user explicitly revalidates them.
- The first-run model page separates hardware-compatible Ollama acquisition from existing LM Studio/GGUF registration. It shows provider, approximate size, storage destination, available space, and safety reserve before any model action.
- The default path still installs the helper without downloading, loading, validating, moving, or deleting a model. Every acquisition or validation remains a separate named confirmation.
- A fresh LM Studio-only setup can complete health, planning, and review without configuring Ollama. Dual-provider installations still check Ollama and refuse foreign loaded models.
- LM Studio load ownership is recorded as soon as the provider returns an instance identity. Any later failure must prove both REST unload and signed-CLI absence before the ownership record is cleared.
- The signed LM Studio CLI namespace is pinned through signature verification and execution. Ollama storage selection rejects symbolic links, junctions, mount points, network targets, and removable drive types, rechecks live free space, and detects path replacement around configuration and acquisition.

## Provider boundaries

Ollama may be installed from its current official signed Windows installer and may acquire one named catalog model only after explicit approval. LM Studio itself and GGUF files are never downloaded or installed by Thalen; beta.13 registers only an existing catalog-audited local GGUF selected by the user.

Both providers must be owned by the current Windows user and listen only on IPv4 loopback. The MCP server remains a bounded, read-only advisory tool with no filesystem, shell, Git, deployment, publishing, email, or external-system access.

## Upgrade behavior

The installer preserves existing Codex configuration and instructions through timestamped backups, a private dry-run, managed markers, hash-bound surgical merging, idempotent repair, and rollback. It never replaces `config.toml` or `AGENTS.override.md` wholesale. An existing unmarked `local_gpu_reviewer` integration remains protected by default.

## Verification and limitations

Routine automated tests use temporary Codex homes, temporary model fixtures, mocked loopback providers, and mocked LM Studio CLI inventory. They do not download a model or run GPU inference. Real provider validation remains an explicit post-install action on the destination computer.

LM Studio's REST load API returns an instance identity but does not currently let the helper assign its own unique instance name. Do not manually load the same registered model key during a helper review. Different-key, additional-instance, path, identity, and unload mismatches fail closed; the narrow simultaneous same-key race remains documented until the supported runtime offers an identifier contract suitable for this boundary.

This installer is not Authenticode-signed and may trigger Windows SmartScreen. Verify the published SHA-256 checksum and GitHub artifact attestation before running it.
