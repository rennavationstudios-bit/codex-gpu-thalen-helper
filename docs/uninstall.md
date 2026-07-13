# Uninstall and recovery

Use Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall, or:

```powershell
thalen-helper uninstall --yes
```

The lifecycle cleanup disables/cancels review, unloads the selected model when Ollama responds, removes the owned per-user startup entry, restores prior `OLLAMA_MODELS`/`OLLAMA_HOST` values only if the current values are still product-owned, removes only the marked MCP/instruction sections, deletes product state, and writes a concise report in the temporary directory.

Codex authentication, unrelated config/instructions, pre-existing Ollama, and pre-existing models are preserved. Ollama is never automatically uninstalled.

A model is removed only when both conditions hold:

1. State proves this helper downloaded it rather than finding it pre-existing.
2. The user explicitly supplies `--remove-owned-model`.

Backups remain beside modified Codex files. If a current managed file is malformed or its markers cannot be removed safely, uninstall first writes a separate safety copy, preserves the current file byte-for-byte, keeps product state for recovery, and reports `UNINSTALL_MANUAL_CLEANUP_REQUIRED`. It never replaces newer user edits with a stale recorded backup automatically; review the safety copy and timestamped backups before manual recovery.

The model directory is preserved by default. Delete it manually only after confirming no other Ollama application uses it.
