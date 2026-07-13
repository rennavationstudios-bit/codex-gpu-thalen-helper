# Codex configuration and instruction merging

## `config.toml`

The installer resolves the selected `CODEX_HOME`, parses existing TOML, and refuses malformed input or an unmanaged collision at `mcp_servers.local_gpu_reviewer`. It removes only a prior marked helper block, builds the current block, re-parses, creates a timestamped backup, and writes atomically.

The managed entry uses the exact installed executable path and includes:

- `required = false`;
- `enabled_tools = ["local_gpu_health", "local_gpu_review"]`;
- prompt approval by default;
- automatic approval only for passive `local_gpu_health`;
- prompt approval for `local_gpu_review`;
- no parallel tool calls;
- bounded startup/tool timeouts;
- only the product-state directory and loopback Ollama host as explicit environment values.

It never changes the primary model, reasoning effort, authentication, or unrelated MCP servers. After write, TOML is parsed again. A fresh-Codex startup validator can reject the change, which restores the exact original automatically.

## `AGENTS.override.md`

A fresh Codex home receives the public full reliability template. An existing override receives only the marked optional-reviewer section. Reinstall and upgrade replace that section idempotently; unrelated text stays in place. Tier wording is regenerated from the selected model tier.

Uninstall deletes a fresh product-owned override file or removes only the marked section from a pre-existing file. Timestamped backups remain available.

## Test isolation

Repository tests always create a temporary `CODEX_HOME`, product state, install directory, and Unicode/space-containing paths. The release scripts clear real-GPU opt-in and never use a live user Codex home.
