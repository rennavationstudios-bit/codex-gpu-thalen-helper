# Codex configuration and instruction merging

## `config.toml`

The installer resolves the selected `CODEX_HOME`, parses existing TOML, and refuses malformed input. It removes only a prior marked helper block, builds the current block, re-parses, creates a timestamped backup, and writes atomically.

If an unmarked `mcp_servers.local_gpu_reviewer` table already exists, setup treats it as user-owned. The exact file bytes are preserved, no backup is needed because no TOML write occurs, and no duplicate table is added. The helper records preservation mode and does not configure Ollama, change model storage, add startup, pull or run a model, enable/disable the table, or expose product controls for that integration. Uninstall likewise leaves the existing integration and runtime untouched.

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

A fresh Codex home receives only the marked local GPU guidance by default. An existing override retains all unrelated instructions. If equivalent unmarked text already references `local_gpu_reviewer`, it is preserved and no duplicate local GPU section is added.

The broader public reliability baseline is a separate, sanitized template and a separate managed section. It is unchecked by default. The interactive setup page shows the computed full before/after diff before the user can choose installation. Setup binds consent to the exact source-byte and planned-output SHA-256 values; any file edit or tier/plan drift rejects the operation before runtime setup and requires a fresh preview. Opting in adds generalized rules for Goal/Context/Constraints/Done framing, task ledgers, interruption recovery, plans and acceptance criteria, subagent boundaries, independent verification, repository safety, privacy, honest completion reporting, local-review announcements, and GPU contention. It contains no user-specific paths, identities, projects, private instructions, model choices, backups, or secrets.

Reinstall and upgrade replace each selected managed section idempotently. The Control Center's Reliability baseline action always shows a new diff before adding or removing only that managed section. Every changed existing file receives a timestamped backup. Writes are atomic, a failed write restores the original bytes including its original BOM/encoding, and uninstall is a surgical rollback that removes only the two product markers while preserving later user edits.

Uninstall deletes a fresh product-owned override only when no user content remains, or removes only the marked sections from a pre-existing file. Timestamped backups remain available.

## Test isolation

Repository tests always create a temporary `CODEX_HOME`, product state, install directory, and Unicode/space-containing paths. The release scripts clear real-GPU opt-in and never use a live user Codex home.
