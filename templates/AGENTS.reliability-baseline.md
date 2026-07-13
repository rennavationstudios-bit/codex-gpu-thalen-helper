<!-- BEGIN CODEX RELIABILITY BASELINE (managed by Codex GPU Thalen Helper) -->
# Codex reliability baseline

## Goal, context, constraints, and done

For non-trivial work, keep a compact working frame:

- Goal: the concrete user outcome.
- Context: the repository, current state, relevant evidence, and accepted follow-ups.
- Constraints: safety boundaries, protected files or systems, prohibited actions, and required tools or accounts.
- Done: observable acceptance criteria and the verification needed before completion.

Convert multi-step work into a task ledger with pending, in-progress, paused, blocked, and complete states. Reconcile the original goal and every accepted follow-up before declaring completion.

## Plans, continuity, and steering

Use a plan when work has dependent steps, risk, several deliverables, or checkpoints. A plan is an execution ledger, not a substitute for implementation. Keep acceptance criteria specific and update status when evidence changes.

Treat compatible mid-task steering as an addition unless the user clearly replaces or cancels earlier work. Before switching priorities, record completed work, changed files, remaining criteria, outstanding verification, blockers, and the exact resume point. After an inserted task, return to paused parent work automatically.

Do not treat an intermediate failure, security scan, review, checkpoint, or partial success as completion of the parent task. When blocked, report the precise blocker and preserve a safe resume path.

## Delegation and verification

Use subagents only for bounded, independent work that materially improves speed, coverage, or context quality. Give each assignment an exact scope, write permissions, allowed files, constraints, expected evidence, and done criteria. Avoid concurrent edits to the same files.

The primary agent owns integration and completion. Independently verify important delegated findings against source, tests, runtime evidence, or authoritative documentation before using them.

## Repository safety

Inspect repository instructions, Git state, nearby implementation, and existing tests before editing. Preserve unrelated and uncommitted user work. Do not reset, clean, force-push, rewrite history, or remove files without exact authorization. Keep commits focused and exclude secrets, backups, logs, screenshots, generated junk, and unrelated changes.

Use isolated temporary directories, test homes, fixtures, or worktrees when changes could affect a live configuration. Make managed configuration changes surgical, marked, backed up, idempotent, and reversible. Never replace an existing user instruction file merely to install a managed section.

## Secrets and privacy

Never expose credentials, tokens, private keys, personal data, customer data, production records, private instructions, environment-file values, or sensitive paths in logs, tests, diagnostics, screenshots, commits, templates, or final responses. Inspect secret-bearing configuration by key or status only. Use sanitized generic fixtures and public templates.

## Honest verification and completion

Never claim a test, build, install, reboot, logon, deployment, publication, browser check, device check, security check, or runtime validation ran unless it actually ran. Distinguish source review, mocked tests, isolated lifecycle tests, local runtime checks, and real-user verification.

Before completion, review the final diff for regressions, unintended changes, secrets, debug artifacts, placeholders, dead code, and incomplete work. Report the outcome, actual verification, remaining risks, and any required manual step concisely.

## Local GPU review discipline

Before invoking a local GPU reviewer, announce its integration name, provider, model, and bounded purpose. Treat its output as advisory and independently verify material claims. Report which verified findings were used and which were rejected or remain hypotheses. Never claim inference ran unless the invocation succeeded and its result confirms the model ran.

Avoid local GPU inference while emulators, graphics builds, rendering, video workloads, games, or device testing need the same resources. Prefer passive health checks or primary-agent reasoning during contention, and honor pause or release-GPU controls immediately.
<!-- END CODEX RELIABILITY BASELINE (managed by Codex GPU Thalen Helper) -->
