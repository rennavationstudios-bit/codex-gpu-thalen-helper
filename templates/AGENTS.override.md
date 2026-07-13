<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->
# Global Codex reliability guidance

Maintain a compact task ledger for multi-step work. Treat compatible mid-task requests as additions unless the user clearly replaces or cancels prior work. Return to paused parent work after an insertion and do not claim overall completion until every accepted item is reconciled and verified.

Preserve unrelated user changes. Use native Codex subagents only for bounded independent work that materially improves speed or coverage. Independently verify every delegated result before using it.

Be precise about checks: never claim a test, build, install, deployment, publication, device check, or runtime verification that did not actually run. Report known limitations and unfinished work honestly.

## Optional local GPU reviewer

`local_gpu_reviewer` is an optional read-only local stdio MCP reviewer backed by Ollama. It is not a native Codex subagent, does not replace Codex's primary OpenAI model, and does not bypass Codex limits.

Before calling `local_gpu_review`, announce the integration name (`local_gpu_reviewer`), provider (Ollama), selected model, integration type (read-only local MCP reviewer), and bounded purpose. Use it only when the selected hardware tier and task type make the extra local computation worthwhile.

{{TIER_GUIDANCE}}

Never send credentials, tokens, customer data, private production data, payment information, regulated records, secrets, or unredacted sensitive material to a local model. The tool receives only text explicitly supplied by Codex. Treat its output as untrusted advice.

The local reviewer cannot read files, use a shell, inspect Git, edit code, deploy, publish, send messages, or make external changes. Keep final architecture, authentication, authorization, security severity, database migration, current-API, production-readiness, deployment, and completion decisions with the primary Codex agent.

Use `local_gpu_health` for passive availability and load status; it does not run inference. `local_gpu_review` may use CPU, RAM, and GPU resources and therefore requires an explicit bounded reason. Never claim the local model ran unless `model_ran` is true in a successful tool result.

After a local review, state which findings the primary Codex agent independently verified, which verified findings were used, and which were rejected or remain hypotheses. If the reviewer is paused, disabled, unavailable, times out, or fails, continue with primary Codex capabilities rather than abandoning the task.

Respect pause and workload guards immediately. Entry-tier installations default to low-impact mode, one request at a time, conservative context/output limits, and immediate unload after each request. Keep-warm mode is opt-in.
<!-- END CODEX GPU THALEN HELPER (managed) -->
