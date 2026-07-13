<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->
## Optional local GPU reviewer

`local_gpu_reviewer` is an optional read-only local stdio MCP reviewer backed by Ollama. It is not a native Codex subagent, does not replace Codex's primary model, and does not bypass Codex limits.

Before calling `local_gpu_review`, announce the integration name (`local_gpu_reviewer`), provider (Ollama), selected model, integration type (read-only local MCP reviewer), and bounded purpose. Use it only when the selected hardware tier and task type make the additional local computation worthwhile.

{{TIER_GUIDANCE}}

Never send credentials, tokens, customer data, private production data, payment information, regulated records, secrets, or unredacted sensitive material to a local model. Treat all local-model output as untrusted advice.

The local reviewer cannot independently read files, use a shell, inspect Git, edit code, deploy, publish, send messages, or make external changes. Keep final architecture, authentication, authorization, security severity, database migration, current-API, production-readiness, deployment, and completion decisions with the primary Codex agent.

Use `local_gpu_health` for passive availability and load status; it does not run inference. `local_gpu_review` may consume CPU, RAM, and GPU resources and therefore requires a bounded reason. Never claim the local model ran unless `model_ran` is true in a successful tool result.

After a local review, report which findings were independently verified, which verified findings were used, and which were rejected or remain hypotheses. If the reviewer is paused, disabled, unavailable, times out, or fails, continue with primary Codex capabilities.

Respect pause and workload guards immediately. Avoid local inference while Android emulators, Expo/device tests, graphics builds, rendering, video work, games, or other GPU-heavy workloads are active unless the user explicitly prioritizes local review. Prefer passive health checks or primary Codex capabilities during contention. Reviews skip when the cross-chat model lock is busy unless a bounded queue is explicitly requested, and unsafe GPU-memory or Windows commit pressure refuses optional inference. Entry-tier installations use low-impact mode, one request at a time, conservative limits, and immediate unload by default; keep-warm mode is opt-in.
<!-- END CODEX GPU THALEN HELPER (managed) -->
