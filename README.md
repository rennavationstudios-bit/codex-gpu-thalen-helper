# Codex GPU Thalen Helper

Use a hardware-appropriate local Ollama model as an optional, bounded, read-only GPU reviewer for Codex on Windows.

> **Independent community project:** This project is not made, endorsed, or supported by OpenAI or Ollama. Codex, ChatGPT, OpenAI, Ollama, Qwen, NVIDIA, AMD, and Intel are trademarks or names of their respective owners.

Codex GPU Thalen Helper keeps Codex as the primary agent. It adds a local stdio MCP server named `local_gpu_reviewer`, which accepts only text Codex explicitly supplies and sends ordinary text-generation requests to an Ollama loopback endpoint. The local result is untrusted advice; Codex still orchestrates, verifies, integrates, and makes final decisions.

The helper does not replace Codex's OpenAI model, bypass Codex limits, or guarantee lower costs, reduced credits, less OpenAI usage, faster work, or better results. Small local models have sharply limited reasoning and code-review ability.

## Why this architecture

Some native custom-agent routes can send a Responses API input item with type `agent_message`. Ollama versions that do not implement that input type reject it. This project avoids that compatibility path:

```text
Codex primary agent
  -> read-only local stdio MCP reviewer
  -> Ollama ordinary text-generation API on 127.0.0.1
  -> conservatively selected local model
```

The MCP reviewer is not described or configured as a native Codex subagent.

## What is included

- A per-user Windows setup wizard and Control Center.
- A self-contained x64 CLI (`thalen-helper.exe`).
- A self-contained stdio MCP server (`local-gpu-reviewer.exe`).
- Dynamic Windows, CPU, RAM, GPU, driver, VRAM, and storage detection.
- A versioned, audited Ollama model catalog and conservative selector.
- Preservation-first, backed-up, idempotent merging of `config.toml` and `AGENTS.override.md`.
- A sanitized reusable reliability baseline that is installed only through an explicit opt-in preview.
- Pause, resume, immediate GPU release, persistent disable, low-impact, and keep-warm controls.
- Repair, update, doctor, model change, verified model move, diagnostics, and surgical uninstall operations.
- Zero telemetry.

The MCP server exposes only:

- `local_gpu_health` — passive; never runs inference.
- `local_gpu_review` — one bounded advisory generation using explicitly supplied text.

It exposes no filesystem, shell, Git, deployment, publishing, email, arbitrary network, or mutation tool.

## System requirements

- Windows x64. Windows 11 is preferred.
- Windows 10 22H2 can run the helper, but Windows 10 is no longer generally supported by Microsoft and receives an installer warning.
- ARM64 is rejected; this project does not claim Windows ARM GPU acceleration.
- Codex installed and authenticated using any supported Codex method.
- Ollama for Windows. Setup can offer the current official signed Ollama installer when Ollama is missing.
- Enough fixed local disk, RAM, and dedicated VRAM for the selected model.

End users do not need .NET, Node.js, Python, or a development SDK. The release executables are self-contained.

## Install

1. Download `Codex-GPU-Thalen-Helper-Setup.exe`, `SHA256SUMS.txt`, and the matching GitHub attestation from the release.
2. Verify the setup checksum:

   ```powershell
   (Get-FileHash .\Codex-GPU-Thalen-Helper-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
   ```

3. Compare it with the exact installer line in `SHA256SUMS.txt`.
4. Run setup, review the detected hardware/model/storage choices, and authorize any Ollama installation or model download.
   The local GPU guidance is automatic. The broader reliability baseline is unchecked by default and shows an `AGENTS.override.md` before/after diff before installation.
5. Restart Codex after successful setup so the new stdio MCP server is discovered.

### Unsigned beta warning

The initial beta executable is **not Authenticode-signed**. Windows SmartScreen may show an unknown publisher warning. GitHub artifact attestation and SHA-256 checksums help verify provenance and integrity, but neither replaces Authenticode signing. Do not proceed if the checksum or attestation does not match.

GitHub CLI can verify an attestation after downloading the release asset:

```powershell
gh attestation verify .\Codex-GPU-Thalen-Helper-Setup.exe --repo rennavationstudios-bit/codex-gpu-thalen-helper
```

## Authentication and privacy

Local Ollama operation requires no OpenAI API key. Codex itself still requires ChatGPT sign-in or another supported Codex authentication method. The helper never asks for, inspects, transmits, or stores OpenAI authentication.

Prompts and responses are not written to disk. Ollama traffic is restricted to an HTTP loopback URI. Hardware discovery collects no username, hostname, serial number, Windows product identifier, network identifier, or unrelated application inventory. Exported diagnostics are redacted and opt-in.

See [PRIVACY.md](PRIVACY.md) and [docs/privacy-and-security.md](docs/privacy-and-security.md).

## Hardware and model selection

The selector uses dedicated VRAM, currently available VRAM when measurable, context/KV-cache and runtime headroom, system RAM, supported acceleration, laptop limits, and storage reserve. Shared GPU memory is kept separate and never counted as dedicated VRAM.

Approximate catalog tiers are conservative guardrails, not guarantees:

| Tier | Typical safe starting point | Local-review scope |
|---|---|---|
| Entry | 0.5B–1.5B | Repeated patterns, obvious smells, categorization, simple fixtures/edges |
| Mid | 7B | Bounded diff review, test-failure grouping, repository mapping hypotheses |
| High | 14B | Broader bounded review, still advisory |
| Enthusiast | verified 30B-class fit | Stronger bounded review, never final authority |

The NVIDIA MX330 2 GB fixture selects `qwen2.5-coder:1.5b`, uses a reduced context, enables low-impact mode, and never counts Intel shared graphics memory. If runtime validation fails, setup attempts only one smaller safe fallback.

Read [docs/hardware-selection.md](docs/hardware-selection.md), [docs/model-selection.md](docs/model-selection.md), and the auditable [model catalog](model-catalog/models.v1.json).

## Model storage

Setup ranks suitable fixed local NVMe, SSD, unknown fixed media, then HDD with a warning. Removable and network drives are never auto-selected. Space calculations include temporary overhead and a safety reserve, with extra protection for the Windows system volume.

The selected path is stored in product state and persisted as the current user's `OLLAMA_MODELS`. If the path changes, setup unloads active Ollama models, safely restarts Ollama once, and refuses to enable the reviewer unless the selected model manifest is present under that exact directory and the loopback API returns the selected tag.

After a fresh Codex restart, the MCP process also requires its inherited `OLLAMA_MODELS` value to match product state. It checks that path and manifest before contacting Ollama and again immediately before generation, alongside the selected tag/digest and listener checks.

`thalen-helper models move <directory> --yes` copies every model file, verifies size and SHA-256, activates and runtime-checks the new directory, then removes the old copy. Failure rolls back the path and preserves the source.

## Automatic Ollama startup

Setup offers per-user automatic startup after sign-in. The helper owns one HKCU Run entry. At logon it uses a named cross-process startup semaphore, checks the endpoint first, and checks all Ollama processes before launching `ollama serve`. It never launches a second process when an Ollama process already exists; an unhealthy existing process is reported for repair.

Startup verification checks:

- the owned per-user startup entry;
- the loopback endpoint response;
- the persisted `OLLAMA_MODELS` path;
- the selected model manifest beneath that exact path;
- selected-model availability through `/api/tags`;
- the selected model digest against the audited catalog;
- loopback-only listeners on port 11434.

The logon command exits unsuccessfully and disables an enabled reviewer when any of those checks fails. Process cleanup is restricted to the trusted Ollama executable path (and its known sibling application executable), so similarly named unrelated processes are not terminated.

If automatic startup is declined, the helper remains installed and clearly reports that local review requires manually starting Ollama after every sign-in.

## Control Center and CLI

The Control Center shows state, model, expected size, hardware tier, GPU/VRAM, acceleration, load state, model storage/free space, and passive health. It provides test, pause, resume, release, enable/disable, low-impact, keep-warm, change-model, move-models, repair, diagnostics, and uninstall guidance.

```text
thalen-helper status
thalen-helper doctor
thalen-helper enable | disable | pause | resume | release-gpu
thalen-helper low-impact on|off
thalen-helper keep-warm on|off
thalen-helper model recommend [--allow-cpu]
thalen-helper model change <tag> --yes
thalen-helper models move <fixed-local-directory> --yes
thalen-helper repair
thalen-helper test
thalen-helper ollama verify | autostart | install --yes
thalen-helper diagnostics export <output.json>
thalen-helper update [--yes]
thalen-helper uninstall --yes [--remove-owned-model]
```

`pause` rejects new calls, cancels an active helper generation when possible, and unloads the selected model. `release-gpu` unloads without persistently disabling review. `resume` does not preload the model. Local generation is single-flight; low-impact mode unloads immediately.

## Configuration safety

Setup parses TOML, makes timestamped backups, preserves unrelated content, inserts a marked MCP block with `required = false`, an explicit two-tool allowlist, prompt approval for review, automatic approval only for passive health, and re-parses the result. A supplied fresh-Codex validator can roll the edit back automatically.

If an unmarked `mcp_servers.local_gpu_reviewer` table already exists, setup preserves it byte-for-byte, adds no duplicate TOML table, leaves its Ollama/model/startup/runtime behavior untouched, and disables this helper's control operations. Missing non-conflicting instruction sections can still be added safely.

The automatic [local GPU guidance template](templates/AGENTS.local-gpu-reviewer.md) is tier-aware. The separate [sanitized reliability baseline](templates/AGENTS.reliability-baseline.md) generalizes task continuity, Goal/Context/Constraints/Done, planning, delegation, repository safety, privacy, honest verification, local-review announcements, and GPU-contention rules. It is optional, unchecked by default, and installed only after a before/after diff preview. The reviewed source and planned-output hashes must still match at apply time. The Control Center can later preview and surgically add or remove the baseline. Both sections use distinct markers; existing files are never replaced. Reinstall/repair is idempotent, changes create timestamped backups, failures restore the original bytes, and uninstall removes only product-managed sections. See [docs/configuration-merging.md](docs/configuration-merging.md).

## Troubleshooting and uninstall

Start with:

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

See [docs/troubleshooting.md](docs/troubleshooting.md). Uninstall from Windows Settings or run `thalen-helper uninstall --yes`. The uninstaller removes only product-owned MCP/instruction sections, startup entry, state, shortcuts, and binaries. It preserves Codex authentication, unrelated settings, pre-existing Ollama, and pre-existing models. A helper-downloaded model is deleted only with explicit `--remove-owned-model` consent. See [docs/uninstall.md](docs/uninstall.md).

## Build from source

See [docs/building.md](docs/building.md). The short form is:

```powershell
.\eng\test.ps1 -Configuration Release -LockedMode -Coverage
.\eng\package.ps1
```

Builds use pinned NuGet versions and lock files. Release automation pins GitHub Actions to full commit SHAs and creates checksums, an SPDX SBOM, dependency/security analysis, and GitHub artifact attestations. Release tags must match the prerelease version policy, point to `main`, and be governed by the repository's protected `v*` tag ruleset and `release` environment.

## Support and security

- Security reports: [SECURITY.md](SECURITY.md)
- Support boundaries: [SUPPORT.md](SUPPORT.md)
- Contributing: [CONTRIBUTING.md](CONTRIBUTING.md)
- Architecture: [docs/architecture.md](docs/architecture.md)
- Known changes: [CHANGELOG.md](CHANGELOG.md)

Licensed under the [MIT License](LICENSE). Models, Ollama, .NET, Inno Setup, and dependencies retain their separate licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [MODEL_LICENSES.md](MODEL_LICENSES.md).
