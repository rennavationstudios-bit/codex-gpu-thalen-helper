# Codex GPU Thalen Helper v0.1.0-beta.18

This unsigned prerelease is a focused Control Center rendering correction. It does not broaden the reviewer's permissions, network boundary, model-download behavior, provider control, or protected-file ownership.

## Literal labels in the rounded Control Center

- Owner-drawn rounded buttons now display literal ampersands instead of interpreting them as WinForms mnemonic prefixes.
- **Models & storage** therefore renders exactly as written in the installed application, screenshots, and contest demonstrations.
- The shared rounded-button painter uses `TextFormatFlags.NoPrefix`, so the correction applies consistently rather than escaping one label as a special case.
- A regression test binds the custom drawing flags to literal ampersand behavior while retaining `UseMnemonic = false`.

All beta.17 Control Center improvements remain intact: one local-review switch, task-aware Ollama and LM Studio routes, responsive bounded toggles, compact GPU status, rounded controls, hover help, and the quiet advanced menu.

## Compatibility, startup, and release evidence

Codex GPU Thalen Helper is an independent community project for Windows x64. It is not made, endorsed, or supported by OpenAI, Ollama, or LM Studio. Windows 11 is preferred; Windows 10 22H2 receives an end-of-general-support warning.

Installation never downloads, registers, loads, or runs a model without a separate reviewed choice. Ollama model storage is selected only after local free-space checks and explicit confirmation. LM Studio remains user-controlled: this release can register only the exact existing catalog-audited Qwythos GGUF and never downloads, copies, moves, or substitutes it.

Optional Ollama startup is per-user after sign-in. The managed launcher uses the existing loopback endpoint and process inventory to avoid duplicate processes; if the user declines startup, Ollama reviews require manual startup after each sign-in. LM Studio and its loopback server are not started or reconfigured by the helper.

The beta.18 release audit builds the self-contained x64 binaries and installer, runs all 358 isolated automated tests, audits NuGet vulnerabilities, scans tracked release inputs for secrets and personal paths, compiles the installer, and generates the SPDX SBOM and friend bundle. The dedicated CI lifecycle job (or an explicit `release-audit.ps1 -RunInstallerLifecycle` run) exercises installer upgrades against temporary Codex homes and mocked provider boundaries. Routine tests download no model and run no GPU inference.

All prior loopback enforcement, no-automatic-download behavior, foreign-model preservation, single-review concurrency, pressure refusal, exact-file LM Studio registration, helper-owned unloading, backups, hash-bound protected-file repair, idempotent updates, and rollback remain in force.

Software cannot prove PSU capacity, PCIe power cabling, card clearance, cooling, firmware compatibility, or every GPU/driver combination. It also cannot simulate a real reboot or sign-in on every destination computer. Destination post-install checks therefore remain authoritative for startup, selected-model availability, storage path, provider loopback status, current pressure, and real hardware compatibility.

The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
