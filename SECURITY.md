# Security policy

## Supported versions

Security fixes target the latest published prerelease until a stable release exists.

## Report a vulnerability

Use GitHub's private vulnerability reporting feature for this repository. Do not open a public issue containing exploit details, credentials, private source, prompts, customer data, or machine identifiers.

Include the affected version, Windows version/architecture, a minimal reproduction, impact, and whether the issue requires a non-default setting. Redact usernames, absolute personal paths, model prompts/responses, tokens, and private repository content.

Maintainers will acknowledge a report when possible, validate it, coordinate remediation, and credit reporters who request credit. This community project offers no guaranteed response SLA or bug bounty.

## Security boundaries

- The MCP server is local stdio only and opens no listener.
- Ollama endpoints must resolve to loopback HTTP and port 11434 is checked for non-loopback listeners.
- Reviewer activation requires the selected model tag, manifest location, and catalog digest to match the configured model storage.
- The reviewer accepts only supplied text, bounds input/output/context/response sizes, permits one generation at a time, and writes no prompts or responses.
- MCP exposes no arbitrary file, shell, Git, patch, deployment, publishing, email, credential, or external-mutation capability.
- Configuration edits are marked, backed up, parsed, idempotent, and surgically removable.
- Update downloads are accepted only from approved HTTPS GitHub asset hosts and must match the release SHA-256 file.
- Ollama installer downloads come from the official release, require the published checksum, and require a valid Windows signature with the expected publisher.
- Releases require a protected `v*` tag ruleset, a protected `release` environment, exact event/tag/commit equality, and `main` ancestry before privileged publication.

Read the full [threat model](docs/threat-model.md) and [secure development notes](docs/secure-development.md).

## Code signing

The v0.1.0 beta is unsigned. Checksums and GitHub attestations do not replace Authenticode. No signing key or certificate is stored in this repository.
