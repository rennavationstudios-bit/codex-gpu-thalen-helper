# Privacy and security design

## Data minimization

No telemetry, analytics, crash upload, tracking, ads, affiliate links, or prompt/response logging exists. Hardware collection is limited to performance/safety inputs and omits usernames, hostname, serials, Windows license/product identifiers, network identifiers, and unrelated applications.

State contains operational paths because Ollama/Codex integration requires them. Voluntary diagnostics replace paths with role labels or base names and exclude prompts, responses, credentials, tokens, and identifiers.

## Network policy

- Reviewer traffic: only an HTTP loopback URI whose exact connected Windows process is verified before request bytes are sent. Ollama itself does not require an HTTP credential.
- Ollama install: explicit user action, official release API/assets, published SHA-256, Authenticode, expected publisher.
- Model pull: explicit install/change action through Ollama's registry behavior.
- Project update: explicit command/UI action, exact GitHub repository, approved HTTPS GitHub asset hosts, required SHA-256 match.
- No helper-originated OpenAI call.

The MCP server is stdio-only. The helper sets `OLLAMA_HOST=127.0.0.1:11434`, verifies active listeners are loopback, and refuses to enable after exposure is detected. Each new production HTTP connection is opened through a guarded transport that maps the exact TCP tuple to its Windows process, requires the current Windows user, accepts only `ollama.exe` or `ollama app.exe`, and verifies the valid `Ollama Inc.` Authenticode publisher before any HTTP request or review prompt is written. Activation also requires the configured model directory, manifest, selected tag, and audited digest to agree. The logon verifier fails closed and persists a disabled state if those checks drift.

Generation is single-flight through a named per-user Windows semaphore. Busy calls skip by default or may use a bounded queue. Before each generation, the helper reads only aggregate dedicated-VRAM and Windows memory/commit counters and refuses optional inference under unsafe pressure. It never pauses, terminates, or reconfigures unrelated development, emulator, device, graphics, build, or Codex processes.

## Prompt injection and model output

The review prompt labels supplied text as untrusted and denies tool/filesystem claims. Output is returned as hypotheses, with an explicit requirement for primary Codex verification. The model cannot execute its output or apply patches.

## Build and release

NuGet versions and lock files are committed. CI uses locked restore, least-privilege permissions, dependency review, CodeQL, secret/personal-data scanning, tests, and reproducible packaging. Release workflows pin actions to full commits, emit SHA-256 files and SPDX SBOM, and request GitHub artifact attestations. Privileged tag publication is additionally gated by an active protected `v*` tag ruleset, a protected `release` environment, exact tag/commit equality, and `main` ancestry.

Unsigned status is explicit. No certificate/private key is stored or requested by CI.
