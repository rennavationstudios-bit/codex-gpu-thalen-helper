# Project instructions

- Keep this repository independent from every application repository and personal Codex setup.
- Never read or write a real user Codex home during tests. Every test that touches `config.toml` or `AGENTS.override.md` must use a temporary `CODEX_HOME`.
- Never commit credentials, prompts, responses, diagnostics from a real machine, usernames, hostnames, device identifiers, absolute contributor paths, or proprietary data.
- Do not run Ollama inference in routine tests. Mock the loopback API by default and gate real GPU tests behind an explicit opt-in environment variable.
- The MCP server is a bounded read-only advisory tool, not a native Codex subagent. It must expose no filesystem, shell, Git, deployment, email, or mutation tool.
- Keep Ollama loopback-only. Reject non-loopback endpoints before any request.
- Preserve unrelated Codex configuration and instructions. Configuration tests must cover backup, parsing, idempotency, rollback, repair, and surgical uninstall.
- Run `eng/test.ps1` before committing. Run `eng/release-audit.ps1` before tagging or publishing.
- Unsigned builds must remain prereleases and must disclose SmartScreen warnings.
