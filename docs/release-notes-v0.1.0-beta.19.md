# Codex GPU Thalen Helper v0.1.0-beta.19

This unsigned prerelease makes the local reviewer contract task-aware and machine-readable while preserving its bounded, read-only advisory role. It does not broaden filesystem, shell, Git, deployment, network, model-download, protected-file, or provider-control authority.

## Task-aware routing and review contracts

- Automatic planning now classifies supported review work deterministically: an explicit task kind wins, then bounded focus and assignment phrases, followed by the existing conservative input-size fallback.
- Task and effort capability floors combine with the audited model catalog. Deep repository analysis can retain the strongest suitable route, while GPU-intensive work continues to prefer the smallest safe eligible model.
- Log triage, test-failure analysis, diff review, repository analysis, edge-case generation, and general review each receive a focused rubric grounded only in text explicitly supplied by Codex.
- Review responses preserve the original raw `findings` while adding bounded `structuredFindings` with ID, claim, supplied location, supplied evidence, confidence, impact, independent verification, and false-positive condition fields.
- `structuredFindingsStatus` distinguishes valid parsing, partial parsing with rejected items, malformed output, and reviews that did not run. An empty array is never presented as independently confirmed correctness.
- Automatic health now reports the eligible loopback provider pool and **Task-aware pool** instead of implying that one stored Ollama model is the route for every task. Passive `local_gpu_plan` remains authoritative for the actual provider and model selected for a request.
- Automatic health degrades to a validated Ollama pool when optional LM Studio is closed or untrusted, without a duplicate LM inventory probe. Eligibility remains provider-qualified even if two providers expose the same model tag.
- Control Center readiness accepts the new bounded structured readiness contract while retaining compatibility with the legacy raw readiness token.

All local-model conclusions remain untrusted advisory material. `ConfirmedObservations` remains empty until the primary Codex agent independently verifies a claim against source code, tests, runtime evidence, or authoritative documentation.

## Compatibility, startup, and release evidence

Codex GPU Thalen Helper is an independent community project for Windows x64. It is not made, endorsed, or supported by OpenAI, Ollama, or LM Studio. Windows 11 is preferred; Windows 10 22H2 receives an end-of-general-support warning.

Installation never downloads, registers, loads, or runs a model without a separate reviewed choice. Ollama model storage is selected only after local free-space checks and explicit confirmation. LM Studio remains user-controlled: the helper registers only exact catalog-audited existing files and never silently downloads, copies, moves, or substitutes them.

Optional Ollama startup is per-user after sign-in. The managed launcher uses the existing loopback endpoint and process inventory to avoid duplicate processes; if the user declines startup, Ollama reviews require manual startup after each sign-in. LM Studio and its loopback server are not started or reconfigured by the helper.

The beta.19 release audit builds the self-contained x64 binaries and installer, runs all 385 isolated automated tests, audits NuGet vulnerabilities, scans tracked release inputs for secrets and personal paths, compiles the installer, and generates the SPDX SBOM and friend bundle. The dedicated CI lifecycle job, or an explicit `release-audit.ps1 -RunInstallerLifecycle` run, exercises installer upgrades against temporary Codex homes and mocked provider boundaries. Routine tests download no model and run no GPU inference.

All prior loopback enforcement, no-automatic-download behavior, foreign-model preservation, single-review concurrency, pressure refusal, exact-file LM Studio registration, helper-owned unloading, backups, hash-bound protected-file repair, idempotent updates, and rollback remain in force.

Software cannot prove PSU capacity, PCIe power cabling, card clearance, cooling, firmware compatibility, or every GPU and driver combination. It also cannot simulate a real reboot or sign-in on every destination computer. Destination post-install checks therefore remain authoritative for startup, selected-model availability, storage path, provider loopback status, current pressure, and real hardware compatibility.

The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
