<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->
## Optional local GPU reviewer

`local_gpu_reviewer` is an optional read-only local stdio MCP reviewer backed by a verified loopback provider (Ollama, or an explicitly registered LM Studio model). It is not a native Codex subagent, does not replace Codex's primary model, and does not bypass Codex limits.

For a non-trivial bounded review, call passive `local_gpu_plan` first with the task kind, expected input size, requested effort, and whether a GPU-heavy workload is active. It selects only from installed, audited, digest-matching models that satisfy the hardware and safety reserve; it never downloads, loads, or runs a model. If planning succeeds, announce the integration name (`local_gpu_reviewer`), provider returned by the plan, planned model, integration type (read-only local MCP reviewer), and bounded purpose before calling `local_gpu_review` with the same routing fields. Do not guess or hard-code a model when automatic routing is enabled.

{{TIER_GUIDANCE}}

Never send credentials, tokens, customer data, private production data, payment information, regulated records, secrets, or unredacted sensitive material to a local model. Treat all local-model output as untrusted advice.

The local reviewer cannot independently read files, use a shell, inspect Git, edit code, deploy, publish, send messages, or make external changes. Keep final architecture, authentication, authorization, security severity, database migration, current-API, production-readiness, deployment, and completion decisions with the primary Codex agent.

Use `local_gpu_health` for passive availability and load status and `local_gpu_plan` for passive task routing; neither runs inference. `local_gpu_review` may consume CPU, RAM, and GPU resources and therefore requires a bounded reason. Never claim the local model ran unless `model_ran` is true in a successful tool result, and report the actual returned model rather than the requested model.

After a local review, report which findings were independently verified, which verified findings were used, and which were rejected or remain hypotheses. If the reviewer is paused, disabled, unavailable, times out, or fails, continue with primary Codex capabilities.

Respect pause and workload guards immediately. Avoid local inference while Android emulators, Expo/device tests, graphics builds, rendering, video work, games, or other GPU-heavy workloads are active unless the user explicitly prioritizes local review. Mark such work in the planning/review request so routing limits itself to the smallest safe model. Prefer passive checks or primary Codex capabilities during contention. Reviews skip when the cross-chat model lock is busy unless a bounded queue is explicitly requested, and unsafe GPU-memory or Windows commit pressure refuses optional inference. Entry-tier installations use low-impact mode, one request at a time, conservative limits, and immediate unload by default; keep-warm mode is opt-in.

Automatic routing uses Q4 models by default, reserves at least 1–2 GiB of VRAM, starts quick work at 8K context, normal work at 16K, and deep work at up to 64K only when both the selected model and current hardware permit it. A requested 128K context is experimental until measured for VRAM headroom, prompt speed, and tool reliability. Flash Attention, Q8_0 K/V cache, GPU-resident KV cache, model-provided Jinja templates, cautious GPU offload, and optional CPU placement of MoE experts are runtime preferences when the selected provider exposes safe supported controls; they are not reasons to bypass resource checks. Vision requires the matching projector. Manual tensor routing, no-mmap, TurboQuant, Q3-versus-Q4 performance claims, and similar overrides remain opt-in experiments.

Use one coherent coding objective per local-model context. Checkpoint useful work and start a fresh context between unrelated objectives. Watch long sessions for repeated tool calls, context drift, loops, malformed JSON, repeated prompt processing, and growing memory use. Local-model advice remains untrusted until independently verified against code, tests, runtime evidence, or authoritative documentation.
<!-- END CODEX GPU THALEN HELPER (managed) -->
