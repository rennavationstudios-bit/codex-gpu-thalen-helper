# Uninstall and recovery

Use Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall, or:

```powershell
thalen-helper uninstall --yes
```

A small local install-context file beside the helper executables records only the managed install directory, state directory, and `CODEX_HOME` selected during setup. This lets Inno uninstall reopen the correct state when non-default paths were used. Explicit uninstall CLI paths take precedence. The context is validated against the current install directory and deleted after successful cleanup.

For a product-owned integration, lifecycle cleanup disables/cancels review, observes whether a tracked zero-keep-alive generation has released, removes the owned per-user startup entry, restores prior `OLLAMA_MODELS`/`OLLAMA_HOST` values only if the current values are still product-owned, removes only the marked MCP/instruction sections, deletes product state, and writes a concise report in the temporary directory. It never issues an unload or deletion request by mutable model name; every Ollama runtime is left untouched if release cannot be observed safely.

When setup preserved an existing unmarked `local_gpu_reviewer`, uninstall does not connect to Ollama, unload or delete models, change startup/environment values, terminate reviewer processes, or edit the existing TOML table. It removes only helper-managed instruction sections and product state.

Codex authentication, unrelated config/instructions, Ollama, and all model data are preserved. Ollama is never automatically uninstalled. The legacy `--remove-owned-model` switch is accepted for command compatibility but beta.12 deliberately performs no model deletion because Ollama's delete API targets a mutable name rather than an immutable digest.

Backups remain beside modified Codex files. The automatic local GPU guidance and optional reliability baseline have distinct markers and are removed independently without replacing the file. If a current managed file is malformed, its markers cannot be removed safely, or its current reviewer contract no longer matches the exact helper-owned configuration, uninstall first writes a separate safety copy, preserves the current file byte-for-byte, keeps product state for recovery, and reports `UNINSTALL_MANUAL_CLEANUP_REQUIRED`. It never replaces newer user edits with a stale recorded backup automatically; review the safety copy and timestamped backups before manual recovery.

When the unmanaged content still matches the first product-created backup, uninstall restores that backup byte-for-byte, including its original encoding, line endings, and trailing whitespace. If unrelated content changed after installation, uninstall does not use the older backup and instead removes only the managed markers from the current file.

The model directory is preserved by default. Delete it manually only after confirming no other Ollama application uses it.
