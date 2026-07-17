# Codex GPU Thalen Helper

Use a hardware-appropriate local Ollama or explicitly registered LM Studio model as an optional, bounded, read-only GPU reviewer for Codex on Windows.

> **Independent community project:** This project is not made, endorsed, or supported by OpenAI or Ollama. Codex, ChatGPT, OpenAI, Ollama, Qwen, NVIDIA, AMD, and Intel are trademarks or names of their respective owners.

Codex GPU Thalen Helper keeps Codex as the primary agent. It adds a local stdio MCP server named `local_gpu_reviewer`, which accepts only text Codex explicitly supplies and sends bounded requests to a verified loopback provider. Ollama is the default; LM Studio is opt-in for exact audited GGUF registrations. The local result is untrusted advice; Codex still orchestrates, verifies, integrates, and makes final decisions.

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

- A modern dark-mode setup wizard and Control Center with plain-language status, contextual primary actions, and hover help on every action button.
- A self-contained x64 CLI (`thalen-helper.exe`).
- A self-contained stdio MCP server (`local-gpu-reviewer.exe`).
- Dynamic Windows, CPU, RAM, GPU, driver, VRAM, and storage detection.
- A versioned, audited Ollama model catalog and conservative selector.
- Optional exact-digest LM Studio registration with provider-aware automatic routing, reasoning-off calls, and verified unload.
- Preservation-first, backed-up, idempotent merging of `config.toml` and `AGENTS.override.md`.
- A sanitized reusable reliability baseline that is installed only through an explicit opt-in preview.
- A sanitized post-install Codex handoff that guides read-only discovery, consented setup, and verification on a new computer.
- Pause, resume, immediate GPU release, persistent disable, low-impact, and keep-warm controls.
- Repair, update, doctor, model change, verified model move, diagnostics, and surgical uninstall operations.
- Zero telemetry.

The MCP server exposes only:

- `local_gpu_health` — passive; never runs inference.
- `local_gpu_review` — one bounded advisory generation using explicitly supplied text.

- `local_gpu_plan` is also passive task-aware model/context selection; it never downloads, loads, or runs a model.

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

For the easiest beginner path, download the matching `Codex-GPU-Thalen-Helper-<version>-Friend-Installer.zip`, extract it, and open `1 - START HERE.txt`. Its root contains the clearly named installer, exact checksum command, beginner guide, and a Codex handoff you can drag into a new Codex task.

1. Download `Codex-GPU-Thalen-Helper-Setup.exe`, `SHA256SUMS.txt`, and the matching GitHub attestation from the release.
2. Verify the setup checksum:

   ```powershell
   (Get-FileHash .\Codex-GPU-Thalen-Helper-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
   ```

3. Compare it with the exact installer line in `SHA256SUMS.txt`.
4. Run setup. The dark installer automatically discovers `CODEX_HOME` (falling back to the current user's `.codex`), creates protected backups when files already exist, and idempotently adds only the disabled helper-owned MCP block and sanitized local GPU guidance. Existing unmarked `local_gpu_reviewer` integrations are preserved byte-for-byte, are not tested, and do not receive helper invocation guidance. No model is downloaded or loaded.
5. In the Control Center, choose one of three clear model paths:
   - point to an existing Ollama model folder and validate the selected model;
   - explicitly approve downloading the recommended hardware-safe model; or
   - install the helper now and finish model setup later. This safe default downloads and loads nothing.
6. Review the exact final action. Model storage has a **Browse** button, and the confirmation names the selected model and approximate size. A manifest is treated only as a local hint: the dialog explains that Ollama may repair or download that same model if inventory disagrees. Guided setup never switches to or downloads a different fallback model. The broader reliability baseline is unchecked by default and shows an `AGENTS.override.md` before/after diff before installation.
7. Restart Codex after successful helper-owned setup so the new stdio MCP server is discovered. If an existing unmarked integration is detected, it is protected and no helper-owned restart is claimed.
8. To let Codex finish or verify setup, drag `CODEX-HANDOFF.md` into a new Codex task, or open **Start > Codex GPU Thalen Helper > Codex setup handoff** and paste the whole document into Codex.

### Unsigned beta warning

The initial beta executable is **not Authenticode-signed**. Windows SmartScreen may show an unknown publisher warning. GitHub artifact attestation and SHA-256 checksums help verify provenance and integrity, but neither replaces Authenticode signing. Do not proceed if the checksum or attestation does not match.

GitHub CLI can verify an attestation after downloading the release asset:

```powershell
gh attestation verify .\Codex-GPU-Thalen-Helper-Setup.exe --repo rennavationstudios-bit/codex-gpu-thalen-helper
```

## Authentication and privacy

Local Ollama operation requires no OpenAI API key. Codex itself still requires ChatGPT sign-in or another supported Codex authentication method. The helper never asks for, inspects, transmits, or stores OpenAI authentication.

Prompts and responses are not written to disk. Ollama traffic is restricted to an HTTP loopback URI, and the production transport verifies the exact connected process as a current-user, validly signed Ollama executable before it sends HTTP bytes. Hardware discovery collects no username, hostname, serial number, Windows product identifier, network identifier, or unrelated application inventory. Exported diagnostics are redacted and opt-in.

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

Hardware fixtures cover integrated graphics, small dedicated GPUs, mainstream cards, large-VRAM cards, multiple GPUs, laptops, unsupported acceleration, and CPU-only systems. They are regression boundaries, not preferred hardware. Production selection is dynamic for the current user's measured dedicated/available VRAM, system RAM, acceleration route, storage headroom, and installed audited models with a current exact-digest validation pass on that installation; shared graphics memory is never counted as dedicated VRAM. Validation stores only bounded timing/acceleration evidence and never prompts or responses. Guided setup keeps the user-confirmed tag and stops safely if validation fails; it never downloads a different fallback model without a new selection.

Read [docs/hardware-selection.md](docs/hardware-selection.md), [docs/model-selection.md](docs/model-selection.md), [docs/task-aware-routing.md](docs/task-aware-routing.md), and the auditable [model catalog](model-catalog/models.v1.json).

## Model storage

Automatic storage selection uses only suitable non-removable local volumes. An explicitly chosen, currently mounted fixed-volume USB or other external model directory is allowed when it has enough reserve space; setup warns that the same drive letter must remain connected and unlocked before Ollama or Codex starts. Network shares and non-fixed removable media remain blocked.

Setup ranks suitable fixed local NVMe, SSD, unknown fixed media, then HDD with a warning. Removable and network drives are never auto-selected. Space calculations include temporary overhead and a safety reserve, with extra protection for the Windows system volume.

The selected path is stored in product state and persisted as the current user's `OLLAMA_MODELS`. If the path changes, setup unloads active Ollama models, safely restarts Ollama once, and refuses to enable the reviewer unless the selected model manifest is present under that exact directory and the loopback API returns the selected tag.

After a fresh Codex restart, the MCP process also requires its inherited `OLLAMA_MODELS` value to match product state. It checks that path and manifest before contacting Ollama and again immediately before generation, alongside the selected tag/digest and listener checks.

`thalen-helper models move <directory> --yes` copies every model file, verifies size and SHA-256, activates and runtime-checks the new directory, then removes the old copy. Failure rolls back the path and preserves the source.

`thalen-helper models activate <directory> --yes` is the non-destructive path for a pre-copied model store. It requires an exact path, size, metadata, and SHA-256 match, runtime-checks the new directory, refuses to unload a foreign model, and always preserves the old directory.

If Windows or the CLI exits during activation, the durable transition marker keeps local review paused. `thalen-helper models recover --yes` re-verifies both trees and restores the original path without deleting either copy.

## Automatic Ollama startup

Setup offers per-user automatic startup after sign-in. Before adding its HKCU Run entry, the helper checks other per-user Run values and Startup-folder entries for Ollama. If another startup owner already exists, it preserves that owner and does not add a duplicate helper entry, but reports the external source as unverified instead of claiming automatic startup is configured. Review or remove the external launcher, or choose manual startup, before enabling managed review. At logon the managed launcher uses a named cross-process startup semaphore, checks the endpoint first, and checks all Ollama processes before launching `ollama serve`. It never launches a second process when an Ollama process already exists; an unhealthy existing process is reported for repair.

Startup verification checks:

- the helper-owned per-user startup entry; name-only external Run/Startup matches are preserved but never certified;
- the loopback endpoint response;
- the persisted `OLLAMA_MODELS` path;
- the selected model manifest beneath that exact path;
- selected-model availability through `/api/tags`;
- the selected model digest against the audited catalog;
- loopback-only listeners on port 11434.

The logon command exits unsuccessfully and disables an enabled reviewer when any of those checks fails. Process cleanup is restricted to the trusted Ollama executable path (and its known sibling application executable), so similarly named unrelated processes are not terminated.

If automatic startup is declined, the helper remains installed and clearly reports that local review requires manually starting Ollama after every sign-in.

## Control Center and CLI

The dark Control Center leads with one contextual action and groups the remaining controls by purpose. It shows state, model, expected size, hardware tier, GPU/VRAM, acceleration, load state, model storage/free space, and passive health. Every action button has hover help that explains its effect, whether it can run inference, and whether a Codex restart may be needed.

```text
thalen-helper status
thalen-helper doctor
thalen-helper enable | disable | pause | resume | release-gpu
thalen-helper low-impact on|off
thalen-helper keep-warm on|off
thalen-helper model recommend [--allow-cpu]
thalen-helper model routing status|automatic|pinned
thalen-helper model change <tag> --yes
thalen-helper models move <fixed-local-directory> --yes
thalen-helper models activate <existing-fixed-local-directory> --yes
thalen-helper models recover --yes
thalen-helper repair
thalen-helper repair --dry-run --diff-out <private-local-file> [--migrate-existing]
thalen-helper repair [--migrate-existing] --expected-config-source-sha256 <hash> --expected-config-planned-sha256 <hash> --expected-agents-source-sha256 <hash> --expected-agents-planned-sha256 <hash>
thalen-helper test
thalen-helper ollama verify | autostart | install --yes
thalen-helper diagnostics export <output.json>
thalen-helper update [--yes]
thalen-helper uninstall --yes [--remove-owned-model]
```

`pause` is temporary: it rejects new calls, cancels an active helper generation when possible, and unloads the selected model while leaving the Codex MCP entry configured. `resume` verifies safety and allows calls again without preloading the model. `disable` persistently turns off the helper-owned MCP entry and may require a Codex restart to remove its tools from the current session; `enable` persistently turns it back on after safety checks. `release-gpu` only unloads the model and leaves future reviews allowed.

Local generation is single-flight through a named per-user Windows semaphore shared by every Codex chat. A review skips immediately by default when busy, or may request a bounded queue of at most 120 seconds. Immediately before generation, the helper refuses optional work when measured dedicated VRAM, physical memory, or Windows commit reserve is unsafe. Low-impact mode unloads immediately; bounded idle keep-alive is opt-in.

Interactive setup leaves model download and validation unchecked. Installing the helper does not automatically pull or load a model; inference occurs only after a separate explicit validation choice or a later review call. The helper never pauses, terminates, or reconfigures Expo, Android, iPhone, graphics, build, or Codex processes. Workload guards and pressure refusal skip optional review instead.

## Configuration safety

Setup parses TOML, makes timestamped backups, preserves unrelated content, inserts a marked MCP block with `required = false`, an explicit three-tool allowlist, prompt approval for review, automatic approval only for passive health and routing, and re-parses the result. A supplied fresh-Codex validator can roll the edit back automatically.

If an unmarked `mcp_servers.local_gpu_reviewer` table already exists, setup preserves it byte-for-byte by default, adds no duplicate TOML table or local-review invocation guidance, leaves its Ollama/model/startup/runtime behavior untouched, and disables this helper's managed controls. The Control Center labels the entry external and unverified. A separate explicit migration is available only after a private dry-run diff is reviewed and all four source/planned hashes for `config.toml` and `AGENTS.override.md` are supplied at apply time. Ambiguous or interleaved TOML table layouts are refused. The optional reliability baseline remains separately available only through its explicit diff-preview flow.

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
