# Codex GPU Thalen Helper v0.1.0 beta 3

This unsigned Windows x64 prerelease hardens coexistence with existing local reviewers and adds a beginner-friendly shareable installer bundle.

Highlights:

- Exact live ownership checks before managed controls, model operations, inference tests, repair, or runtime cleanup.
- Existing unmarked reviewers remain external and unverified; the helper adds no duplicate TOML table or invocation guidance and disables controls that cannot safely apply.
- Passive warnings for non-loopback Ollama listeners, with enable and resume blocked until loopback-only verification passes.
- Existing per-user Ollama Run and Startup-folder entries are detected so the helper does not add another startup owner; unverified external sources are preserved but never reported as configured.
- Managed Codex ownership is now structurally bound to the exact reviewer table inside the unique managed marker span, so displaced markers cannot authorize unrelated TOML edits or removal.
- Model-directory moves recheck ownership before activation and rollback, preserve verified copies on late drift, and never delete both the source and destination during failed reconciliation.
- Drifted protected configuration is backed up and left for explicit review instead of being overwritten or removed automatically.
- A checksummed and attested friend installer release asset with a clearly named root installer, copy-paste checksum command, unsigned-build disclosure, and install/use guide.
- A beginner-oriented `CODEX-HANDOFF.md` that asks Codex to perform passive discovery, guide simple consent choices, prefer the smallest safe model on modest hardware, and verify the integration after restart.
- Automatic first-run expansion of the required model-acquisition page after deferred installation, with required setup headings highlighted in orange.
- Routine tests remain isolated behind temporary `CODEX_HOME` and state directories and do not run Ollama inference.

## Important unsigned beta notice

`Codex-GPU-Thalen-Helper-Setup.exe` and the clearly labeled copy in the friend bundle are not Authenticode-signed. Windows SmartScreen may show an unknown publisher warning. Verify the supplied SHA-256 and GitHub artifact attestation before running the installer.

This project is not made, endorsed, or supported by OpenAI or Ollama.

## Hardware validation limitations

Automated release checks use mocks and temporary isolated Codex homes. They do not reboot a physical Windows PC, sign out and back in, download a real Ollama model, run real GPU inference, or prove behavior on every GPU/driver combination. The installer and helper verify those conditions on the destination computer, refuse unsafe pressure or non-loopback states, and require explicit consent before model acquisition or validation. This unsigned prerelease should be treated as hardware-dependent until the destination user's post-install checks pass.
