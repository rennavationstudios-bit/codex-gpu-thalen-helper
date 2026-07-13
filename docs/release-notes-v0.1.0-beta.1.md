# Codex GPU Thalen Helper v0.1.0 beta 1

Initial independent community beta for Windows x64.

This release adds a hardware-appropriate local Ollama model as an optional, bounded, read-only stdio MCP reviewer for Codex. Codex remains the primary agent and must verify local-model advice. The integration uses ordinary Ollama text generation and avoids the incompatible `agent_message` custom-agent route.

Highlights:

- Dynamic Windows/CPU/RAM/GPU/VRAM/driver/storage detection.
- Conservative, licensed, versioned model catalog and one-step fallback.
- Per-user idempotent Ollama startup with no duplicate process launch.
- Exact `OLLAMA_MODELS`, selected-model tag/digest/manifest, endpoint, and loopback verification with fail-closed activation.
- Backed-up, parsed, idempotent Codex config/instruction merging.
- Preservation-first coexistence: an existing unmarked `local_gpu_reviewer` table is never replaced, duplicated, activated, or used to take over Ollama/model/startup controls.
- Automatic sanitized local-GPU guidance plus a separate optional Codex reliability baseline with an interactive before/after diff, source/plan-hash-bound apply, Control Center add/remove, distinct markers, backup, idempotent upgrades, and surgical rollback.
- WinForms setup/Control Center plus self-contained CLI and MCP executables.
- Pause, resume, immediate GPU release, low-impact, repair, update, model move, and surgical uninstall.
- Zero telemetry; no OpenAI API key requested or stored.
- Locked dependencies, fixture/mocked integration tests, CodeQL/dependency review, SPDX SBOM, checksums, and GitHub build attestation.
- Protected release tags/environment with exact tag, commit, and `main` ancestry verification.

## Important unsigned beta notice

`Codex-GPU-Thalen-Helper-Setup.exe` is **not Authenticode-signed**. Windows SmartScreen may show an unknown publisher warning. Verify both `SHA256SUMS.txt` and the GitHub artifact attestation. Checksums and attestation do not replace Authenticode.

## Known limitations

- Windows x64 only; Windows 11 preferred.
- Small models can miss bugs and produce incorrect advice.
- GitHub-hosted CI validates hardware fixtures, not consumer-GPU execution.
- Exact GPU/partial-GPU labeling depends on Ollama runtime metadata.
- A physical clean-machine, broader AMD/Intel discrete-GPU, and additional low-end-laptop validation matrix remains welcome from the community.

This project is not made, endorsed, or supported by OpenAI or Ollama.
