# Codex GPU Thalen Helper v0.1.0 beta 2

This unsigned Windows x64 prerelease makes first-time setup substantially simpler while preserving the helper's isolation and safety boundaries.

Highlights:

- Modern dark installer, guided setup, and Control Center with a contextual primary action and hover help on every button.
- Automatic protected no-model bootstrap during interactive installation; the base install never downloads or loads a model.
- Persisted custom state and `CODEX_HOME` routing reused across reinstall and repair, with mismatch refusal before any protected-file change.
- Existing unmarked `local_gpu_reviewer` integrations remain preserved and are never duplicated or taken over.
- Explicit model paths for using an existing folder, approving the exact selected model, or finishing setup later.
- Named acquisition confirmation explains when Ollama may repair or download the same selected model; guided setup never downloads a different automatic fallback.
- Per-user Ollama autostart option, loopback-only checks, exact `OLLAMA_MODELS` verification, duplicate-process prevention, and passive fresh-logon health checks.
- Serialized Control Center actions plus single-flight local review, resource-pressure refusal, queue-or-skip behavior, and immediate unload by default.
- Temporary `CODEX_HOME` and product-state test isolation, mock loopback Ollama coverage, locked dependencies, checksums, SPDX SBOM, and release attestation support.

## Important unsigned beta notice

`Codex-GPU-Thalen-Helper-Setup.exe` is not Authenticode-signed. Windows SmartScreen may show an unknown publisher warning. Verify `SHA256SUMS.txt` and the GitHub artifact attestation before running it. Checksums and attestation do not replace Authenticode.

## Compatibility and privacy

- Windows x64 only; Windows 11 is preferred.
- No OpenAI API key is requested or stored.
- Prompts and responses are not logged.
- The MCP server is local stdio only and exposes no filesystem, shell, Git, deployment, email, credential, or mutation tool.
- Real model inference is never part of routine installation or test execution.

This project is not made, endorsed, or supported by OpenAI or Ollama.
