# Codex GPU Thalen Helper v0.1.0-beta.4

This unsigned Windows x64 prerelease closes two locally exploitable trust-boundary gaps found by the completed Codex Security review while preserving the beginner-friendly installer and isolated test policy.

## Security improvements

- Every new production Ollama HTTP connection is mapped to its exact Windows TCP-owner process before request bytes are sent.
- The peer must run as the current Windows user, use the exact `ollama.exe` or `ollama app.exe` name, and carry a valid `Ollama Inc.` Authenticode signature. Arbitrary loopback listeners receive no HTTP request and no review prompt.
- NVIDIA telemetry resolves `nvidia-smi.exe` only from trusted absolute Windows or NVIDIA installation paths. It never searches the current folder or `PATH`.
- Nonstandard NVIDIA layouts safely retain conservative DXGI hardware detection without the enhanced `nvidia-smi` fields.

## Verification

- Routine tests use a temporary `CODEX_HOME` and temporary product-state directory.
- Loopback peer tests use mock listeners and prove rejected peers receive zero HTTP bytes.
- Executable-search tests use inert marker files and never invoke NVIDIA or Ollama tooling.
- No real model is downloaded or loaded during installation or automated release tests.

## Important unsigned beta notice

`Codex-GPU-Thalen-Helper-Setup.exe` and the clearly labeled copy in the friend bundle are not Authenticode-signed. Windows SmartScreen may show an unknown publisher warning. Verify the supplied SHA-256 and GitHub artifact attestation before running the installer.

This project is not made, endorsed, or supported by OpenAI or Ollama.

## Physical-machine limits

Automated release checks do not reboot a destination PC, sign out and back in, download a real model, run real GPU inference, or prove behavior on every GPU/driver combination. Destination post-install verification remains required for startup, selected-model availability, storage path, fresh Codex connection, and loopback-only status.
