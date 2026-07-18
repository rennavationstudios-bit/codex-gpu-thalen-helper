# Codex GPU Thalen Helper v0.1.0-beta.17

This unsigned prerelease is a focused desktop usability update. It does not broaden the reviewer's permissions, network boundary, model-download behavior, or protected-file ownership.

## A cleaner, faster Control Center

- The installed app now matches the compact Thalen AI website panel: one **Local reviews** switch, clear normal/deep and quick/GPU-busy routes, **Test reviewer**, **Models & storage**, and a small GPU status strip.
- Qwythos through LM Studio remains the prominent automatic normal/deep route when its exact registered file is installed and validated. Qwen3 8B through Ollama remains the quick or GPU-busy route on the validated RTX 3090 setup.
- Less-common low-impact, pinned-routing, keep-warm, release, repair, diagnostics, reliability, disconnect, and uninstall controls remain available through the quiet advanced menu.
- Long status and hero copy stays inside the compact card with a clean ellipsis; hovering exposes the full text instead of letting the card widen, hide controls, or produce a horizontal scrollbar.

## Responsive switches without visual artifacts

- Toggle switches are fully owner-drawn controls with rounded keyboard focus, checked-state accessibility, and parent-surface compositing. They no longer inherit rectangular native focus or corner artifacts.
- Passive health and Quick/Standard/Deep route checks run concurrently.
- A switch is locked only for its bounded state mutation; the longer passive refresh no longer disables every switch for roughly twenty seconds.
- Overlapping passive refreshes are generation-checked so an older result cannot overwrite a newer preference.

All prior loopback enforcement, no-automatic-download behavior, foreign-model preservation, single-review concurrency, pressure refusal, exact-file LM Studio registration, helper-owned unloading, backups, hash-bound protected-file repair, idempotent updates, and rollback remain in force.

## Compatibility, startup, and release evidence

Codex GPU Thalen Helper is an independent community project for Windows x64. It is not made, endorsed, or supported by OpenAI, Ollama, or LM Studio. Windows 11 is preferred; Windows 10 22H2 receives an end-of-general-support warning.

Installation never downloads, registers, loads, or runs a model without a separate reviewed choice. Ollama model storage is selected only after local free-space checks and explicit confirmation. LM Studio remains user-controlled: this release can register only the exact existing catalog-audited Qwythos GGUF and never downloads, copies, moves, or substitutes it.

Optional Ollama startup is per-user after sign-in. The managed launcher uses the existing loopback endpoint and process inventory to avoid duplicate processes; if the user declines startup, Ollama reviews require manual startup after each sign-in. LM Studio and its loopback server are not started or reconfigured by the helper.

The beta.17 release audit builds the self-contained x64 binaries and installer, runs all 357 isolated automated tests, audits NuGet vulnerabilities, scans tracked release inputs for secrets and personal paths, compiles the installer, generates the SPDX SBOM and friend bundle, and exercises the installer lifecycle against temporary Codex homes and mocked provider boundaries. Routine tests download no model and run no GPU inference.

Software cannot prove PSU capacity, PCIe power cabling, card clearance, cooling, firmware compatibility, or every GPU/driver combination. It also cannot simulate a real reboot or sign-in on every destination computer. The destination post-install checks therefore remain authoritative for startup, selected-model availability, storage path, provider loopback status, current pressure, and real hardware compatibility.

The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
