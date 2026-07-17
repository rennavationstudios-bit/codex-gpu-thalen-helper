# Codex GPU Thalen Helper v0.1.0-beta.5

This unsigned Windows x64 prerelease adds dynamic task-aware model routing while keeping Codex as the commander and local output as read-only untrusted advice.

## Automatic routing

- New passive `local_gpu_plan` chooses from installed, audited, digest-matching Q4 Ollama models without downloading, loading, or running one.
- Quick, standard, and deep work default to 8K, 16K, and up to 64K context, capped by the chosen model. Context above 64K requires an explicit experimental preference and measurement.
- Routing considers the task kind, estimated input, effort, detected dedicated/available VRAM, system memory, configured 2 GiB reserve, and active GPU-workload hint.
- Selection is repeated inside the cross-process single-review lock immediately before pressure checks and generation.
- Automatic mode unloads after every response and disables keep-warm so sequential tasks can safely choose different models.

## Controls and safety

- The Control Center and CLI expose persistent automatic or pinned routing shared by all Codex projects using the managed integration.
- Pause, release, and disable track and unload the exact active routed model even when it differs from the saved pinned model.
- Pinned mode never silently switches models, still verifies the catalog digest, preserves the configured VRAM reserve, and refuses a large optional model during an active GPU-intensive workload.
- The managed Codex template teaches the passive plan/announce/review workflow and records provider-honest Q4, Flash Attention, Q8 K/V, GPU KV, Jinja, MoE, and experimental guidance.

## Provider boundary

The managed production reviewer remains Ollama-only and loopback-only. LM Studio-only models such as Qwythos or Qwen3.6 are not silently placed in automatic rotation; a future provider adapter must meet the same process trust, loopback, locking, pressure, and ownership-safe unload requirements.

## Verification

Automated tests use temporary product state and temporary Codex homes with mocked Ollama. They do not download, load, or run a real model. This installer is not Authenticode-signed and may trigger SmartScreen; verify the supplied SHA-256 and GitHub artifact attestation.
