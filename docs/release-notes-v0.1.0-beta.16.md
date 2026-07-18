# Codex GPU Thalen Helper v0.1.0-beta.16

This unsigned prerelease is a usability and truthfulness release for the Windows Control Center. The reviewer, provider, ownership, and protected-file safety boundaries remain unchanged.

## A simpler Control Center

- One clear **Local reviews** switch pauses or resumes optional local processing while keeping the Codex integration understandable.
- The home screen shows the actual passively planned normal route prominently, plus current provider eligibility and GPU state. Quick, normal, and deep routing are checked independently without loading a model.
- **Test reviewer** asks before one bounded inference, reports the actual provider and model that ran, requires the exact readiness token, and verifies that any proven helper-owned model was released before reporting success.
- Guided model and storage setup, low-impact mode, and a collapsed advanced section replace the previous wall of paired pause/resume and enable/disable buttons.
- The new Thalen icon is included in the application, setup executable, Windows shortcuts, and uninstall entry. Rounded controls are antialiased and DPI-aware.

## Recovery and status correctness

- **Models & storage** remains available when an incomplete first run has no managed state, while existing external integrations stay protected.
- A transient status failure now exposes a passive **Retry status** action instead of leaving the window inert.
- **Ready** now means the helper state is enabled *and* the managed Codex MCP entry is actually enabled. Turning reviews on repairs a disabled managed entry before telling the user to restart Codex.
- Automatic presentation no longer shows a stale pinned fallback or a catalog download estimate when Qwythos or another installed validated route is the actual plan.

All beta.15 LM Studio input isolation, beta.14 protected ownership reconciliation, beta.13 exact-file Qwythos validation, loopback enforcement, foreign-model preservation, managed backups, hash-bound repair, idempotent updates, and rollback remain in force.

The installer remains unsigned. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
