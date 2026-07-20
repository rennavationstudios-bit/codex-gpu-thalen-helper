# Secure development notes

- Preserve loopback-only and stdio-only boundaries.
- Never add arbitrary file, shell, Git, patch, credential, remote-control, or mutation tools to the MCP server.
- Treat all model input/output as untrusted. Never execute generated text.
- Keep error text sanitized and never log prompts, responses, environment variables, or HTTP bodies in production.
- Keep input, response, context, output, time, download, and redirect bounds.
- Keep structured output additive and shape-bounded. Preserve raw findings for compatibility, never promote parsed model text to confirmed evidence, and do not treat an empty parsed array as proof of a clean review.
- Keep automatic task inference deterministic and passive. Explicit task kinds must override phrase matching, and phrase matching must precede the documented conservative size fallback without contacting a provider.
- Validate model identifiers against the audited catalog for managed operations.
- Keep automatic model selection commercial-license-compatible.
- Require explicit confirmation for downloads, inference tests, model deletion, update launch, and restricted licenses.
- Preserve unrelated user files. Use temporary Codex homes in every test.
- Review dependency licenses/advisories before updates; commit lock-file changes intentionally.
- Keep GitHub Actions pinned and permissions minimal. Never expose release/signing secrets to pull requests.
- Keep the protected `v*` tag ruleset and `release` environment active; never rely on a tag-controlled workflow file as the sole release authorization check.
- An Authenticode certificate must be provided through an approved external signing system; never commit a PFX or password.

Run `.\eng\release-audit.ps1` before a tag. Security findings must be validated, fixed, retested, and reconciled with the entire release checklist.
