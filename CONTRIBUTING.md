# Contributing

Contributions are welcome for Windows x64 detection, accessibility, localization, fixture coverage, installer reliability, and bounded MCP safety.

Before opening a pull request:

1. Read `AGENTS.md`, `SECURITY.md`, and `docs/secure-development.md`.
2. Do not include credentials, prompts/responses, private repositories, personal absolute paths, usernames, hostnames, serials, production data, or generated build output.
3. Keep Ollama loopback-only and do not add arbitrary filesystem, shell, Git, network, or mutation tools.
4. Add or update tests for behavior changes.
5. Run `.\eng\test.ps1 -Configuration Release -LockedMode`.
6. Explain user-visible behavior, security impact, and manual checks in the pull request.

Do not weaken safety checks to make tests pass. New dependencies require a maintained source, pinned version, compatible license, and a clear reason.

By contributing, you agree that your contribution is licensed under the MIT License.
