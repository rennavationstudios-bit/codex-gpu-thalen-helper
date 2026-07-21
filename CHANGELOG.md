# Changelog

All notable changes are documented here.

## Unreleased

## 0.1.0-beta.23 - 2026-07-21

- Made the paste-into-Codex bootstrap work for signed-out public users by listing the exact repository and raw guide URLs instead of asking Codex to construct deep GitHub links.
- Added explicit recovery for connector-specific and guessed-link 404 responses: retry the exact public URL over ordinary unauthenticated HTTPS, never infer that the repository is private from one failed integration, and report the exact failing URL/status if the public endpoint itself fails.
- Added exact release-tag raw URL templates for the install guide and packaged Codex handoff, plus packaging assertions that prevent a friend bundle from omitting the public-access recovery contract.

## 0.1.0-beta.22 - 2026-07-20

- Removed .NET's independent 100-second `HttpClient` timeout from the loopback Ollama and LM Studio clients so each operation's existing bounded timeout is authoritative. Cold Qwythos loads can now use the full audited five-minute load budget instead of being cancelled near 100 seconds.
- Kept bounded failure behavior unchanged: inventory calls still time out after 10 seconds, model generation and LM Studio loading after five minutes, Ollama pulls after two hours, and caller cancellation still releases helper-owned resources.
- Added deterministic no-inference regression coverage proving a slower loopback response is governed by the helper's explicit operation timeout rather than an injected `HttpClient` default.
- Locked the signed LM Studio CLI inventory launch to redirected standard streams, `UseShellExecute=false`, and `CreateNoWindow=true` so passive routing checks do not open a console window.

## 0.1.0-beta.21 - 2026-07-20

- Added a separate display-only review activity signal so Codex-started Ollama and LM Studio work is visible during provider loading, reviewing, releasing, and attention states without weakening strict model-ownership tracking.
- Made the Control Center restore its exact passive status after the bounded activity ends and ignore malformed, future, or expired activity records.
- Kept activity informational: it cannot authorize unload, release, routing, health, configuration, or model-control actions, and it contains no prompt, response, path, digest, user, or machine identity.
- Added mocked LM Studio and Ollama lifecycle coverage, phase/status rendering tests, expiry/privacy tests, and a negative control-action authority test.
- Made temporary uninstall reports collision-safe when independent removals finish during the same second, without changing surgical configuration or model-preservation behavior.

## 0.1.0-beta.20 - 2026-07-20

- Replaced the compact three-dot menu pill with a transparent keyboard-accessible ellipsis glyph, removing its rectangular background and focus-mask artifacts entirely.
- Made the Control Center GPU status reflect helper-owned reviews started by Codex: a lightweight local tracker observer shows the tracked provider and model during the bounded lifecycle, then restores the exact prior passive status after the tracker clears.
- Kept live status passive and low overhead. The observer reads only the helper-owned activity tracker; it does not poll providers, load models, run inference, or interfere with review locking and cleanup.
- Added focused WinForms rendering and active-to-idle status regression coverage.

## 0.1.0-beta.19 - 2026-07-20

- Made automatic task inference meaning-aware without running inference: an explicit task kind still wins, then automatic routing checks deterministic focus and assignment phrases for test-failure, diff-review, repository-analysis, log-triage, and edge-case work before the existing conservative input-size fallback.
- Automatic routing now uses existing catalog task guidance and soft task/effort hardware-tier floors while retaining exact model validation, hardware/resource guards, pinned behavior, LM Studio preference, and the smallest-safe-model rule during GPU-intensive work.
- Added task-specific local-review rubrics, additive `structuredFindings`, and `structuredFindingsStatus` so valid empty, partially rejected, malformed, and not-run results remain distinguishable while preserving the original raw `findings` response.
- Added regression coverage for deterministic task precedence, task-suitable model selection, safe fallback warnings, structured parsing bounds, malformed-output compatibility, and the public MCP schema.
- Clarified automatic health output as a task-aware provider/model pool with eligible loopback endpoints; task planning remains the authoritative per-request route.
- Kept automatic Ollama health available when optional LM Studio is closed or untrusted, reused one captured LM Studio inventory probe, and retained provider-qualified eligibility when model tags collide across providers.

## 0.1.0-beta.18 - 2026-07-18

- Render literal ampersands in every owner-drawn rounded button instead of treating them as hidden WinForms mnemonic prefixes.
- Added a regression test that binds the shared custom painter to `TextFormatFlags.NoPrefix`, preserving labels such as **Models & storage** in screenshots, accessibility review, and normal use.

## 0.1.0-beta.17 - 2026-07-18

- Matched the desktop Control Center to the compact public-site card: one local-review switch, compact task-aware routes, two primary actions, and one GPU status strip. A separate Deep row appears only when it differs from Normal.
- Replaced native checkbox rendering with a fully owner-drawn, keyboard-accessible toggle so selected controls keep clean rounded edges without rectangular focus artifacts.
- Constrained long hero and GPU-status text to the compact card with ellipsis plus full hover detail, and added nested-ancestor layout checks at the minimum and default window sizes.
- Parallelized passive provider planning, cancel superseded passive work, immediately reconcile rejected or reversed toggle changes from persisted state, and decouple toggle mutations from the longer status refresh so one preference change no longer locks every switch for roughly twenty seconds.
- Kept Qwythos through LM Studio prominent for normal and deep work, Qwen3 8B through Ollama for quick or GPU-busy work, and all advanced safety controls behind one quiet menu.

## 0.1.0-beta.16 - 2026-07-18

- Rebuilt the Control Center around one local-review switch, a prominent automatic route, one bounded reviewer test, guided model setup, and a collapsed advanced area.
- Added a branded application, installer, shortcut, and uninstall icon plus antialiased DPI-aware rounded controls that do not use jagged binary regions.
- Display the actual passive Quick, Standard, and Deep routes across Ollama and registered LM Studio models; Qwythos is no longer hidden behind a stale pinned fallback or an expected-download label.
- Made readiness require both helper state and the real managed Codex MCP `enabled` value, with a safe one-click recovery when those states disagree.
- Kept first-run model setup reachable after cancellation, restored passive status retry after transient failures, and added layout/recovery regression coverage.
- Strengthened the Control Center test so it verifies the exact readiness token, reports the actual provider and model, and releases only a proven helper-owned model before passing.

## 0.1.0-beta.15 - 2026-07-18

- Prevented the signed LM Studio inventory CLI from inheriting the MCP server's JSON-RPC standard-input stream.
- Gave each fixed read-only `lms ls --json` or `lms ps --json` process a private input stream and closed it immediately, so Qwythos planning does not time out and silently fall back to Ollama.
- Added a regression test proving the inventory child receives EOF instead of waiting on or consuming parent protocol input.

## 0.1.0-beta.14 - 2026-07-18

- Added an explicit recovery path for the narrow case where helper state records a prior managed reviewer but the current Codex file contains one structurally valid unmarked reviewer after an external rewrite.
- Kept ordinary repair fail-closed: reconciliation requires `--migrate-existing`, a private dry-run diff, and all four source/planned SHA-256 values before either protected file can change.
- Added regression coverage proving ordinary repair still refuses the ownership contradiction, the dry-run is read-only, the hash-bound migration preserves unrelated TOML and instructions, and the result is idempotent.

## 0.1.0-beta.13 - 2026-07-18

- Restored catalog-audited LM Studio/GGUF routing with a signed current-user CLI binding that proves the exact indexed file before and after each bounded load, generation, and exact-instance unload.
- Added a provider-correct first-run experience: hardware-compatible Ollama acquisition choices stay separate from existing LM Studio/GGUF registration, and setup never silently downloads or loads a model.
- Expanded model-storage guidance with a user-selected destination, fixed-volume and free-space reserve checks, approximate download sizes, existing-model paths, and clear opt-in consent.
- Preserved legacy beta.11 LM Studio records as non-routable history until explicit beta.13 revalidation establishes current file identity and validation evidence.
- Made fresh LM Studio-only installations work without an unrelated Ollama model path or daemon while preserving loopback and dual-provider foreign-model guards.
- Added durable provisional LM Studio instance ownership, exact failure-path unload proofs, and recovery evidence when a helper-created instance cannot be proven released.
- Pinned the signed LM Studio CLI executable namespace across signature verification and process execution, and reject redirected Ollama storage paths before configuration or acquisition.

## 0.1.0-beta.12 - 2026-07-18

- Rounded every themed setup and Control Center action button with DPI-aware clipping and borders while preserving hover, disabled, focus, keyboard, and accessibility behavior.
- Added a copy-ready Codex MCP installation handoff that fetches only the exact official GitHub release, verifies checksums and attestations, and retains explicit consent for the unsigned installer and model operations.
- Removed name-based Ollama unload and deletion calls. Reviews and validation request `keep_alive=0s`, then observe release; pause, disable, release, and uninstall never target a mutable model tag destructively.
- Closed provider-restart races so setup, repair, move, activation, recovery, and repair rollback never stop a shared Ollama process while changing `OLLAMA_MODELS`.
- Temporarily disabled LM Studio registration and routing because its loopback inventory does not expose the absolute file behind a loaded model key. Prior validation is invalidated and no inference runs rather than claiming an unprovable file binding.
- Restricted helper-managed MCP environment settings to the exact product allowlist. Unknown `env` or `env_vars` entries are preserved byte-for-byte and require explicit review instead of automatic repair.
- Made repair precompute its exact startup/environment write set, restore only those values on failure, and preserve concurrent edits.

## 0.1.0-beta.11 - 2026-07-17

- Added `OLLAMA_MODELS` to the managed Codex MCP `env_vars` whitelist so restarted stdio reviewer processes receive the helper-verified per-user model-store path.
- Made beta.10 managed blocks without that whitelist fail ownership inspection as repairable drift, with hash-bound repair and idempotency coverage.

## 0.1.0-beta.10 - 2026-07-17

- Ollama review and validation now refuse every loaded model that is not bound to the exact current helper ownership marker and requested route, including untracked same-name models and CPU-only foreign models.
- Pause, disable, release, uninstall cleanup, and validation cleanup unload only a valid tracked helper-owned model; stale, malformed, mismatched, or absent ownership evidence fails closed without a name-based fallback unload.
- Added mocked regression coverage for CPU/GPU foreign models, same-name ambiguity, stale tracking, exact tracked cleanup, and control actions that preserve untracked runtimes.

## 0.1.0-beta.9 - 2026-07-17

- Added `models activate` for an exact, non-destructive switch to a complete pre-copied Ollama store.
- Added revision-bound transition recovery with `models recover`, stale-write protection, and move/repair/control guards while recovery is pending.
- Activation now refuses loaded foreign models and verifies files, empty directories, timestamps, attributes, SHA-256, fixed-volume ancestry, runtime model identity, and source preservation before committing.

## 0.1.0-beta.8 - 2026-07-17

- Fixed post-logon Ollama startup so an unused port is treated as safe to bind on loopback instead of being misclassified as network exposure.
- Preserved fail-closed blocking when an actual wildcard or non-loopback listener owns the Ollama port.
- Added regression coverage for absent, loopback-only, and exposed listener states.

## 0.1.0-beta.7 - 2026-07-17

- Added optional, exact-digest LM Studio routing for audited local GGUF models, beginning with Qwythos 9B BF16.
- Added provider-aware validation and automatic routing: quick and GPU-busy reviews stay on smaller Ollama models while validated standard/deep reviews can prefer Qwythos.
- Added a signed-current-user LM Studio loopback trust check, one shared GPU lease, foreign-model refusal, bounded validation, and verified unload after every LM Studio call.
- Existing Ollama-only installations remain the safe fallback when LM Studio or removable model storage is unavailable.

## 0.1.0-beta.6 - 2026-07-16

- Added read-only protected repair previews with explicit private diff output and four source/planned SHA-256 values that bind both Codex files before either write.
- Added explicit, reviewed migration for an existing unmarked `local_gpu_reviewer`; default installs and repairs still preserve external integrations byte-for-byte.
- Added surgical TOML migration with ambiguity refusal, exact existing AGENTS prefix preservation, timestamped backups, idempotent adoption, and cross-file rollback.
- Added semantic update ordering, correct failed-update exit codes, persisted product-version refresh, and a SHA-pinned disposable beta.5-to-beta.6 upgrade lifecycle.
- Added fail-closed per-model validation evidence so automatic routing uses only exact installed digests that passed the current bounded validation protocol; prompts and responses are never persisted.

## 0.1.0-beta.5 - 2026-07-16

- Added passive `local_gpu_plan` task routing plus automatic or pinned model-selection modes shared across Codex projects.
- Automatic reviews now select an installed, catalog-audited, digest-matching Q4 Ollama model inside the cross-process inference lock using task type, effort, input size, live RAM/VRAM headroom, and GPU-workload hints.
- Added persistent CLI and Control Center routing controls, quick/standard/deep context policy, a 2 GiB VRAM reserve, explicit provider-managed tuning disclosures, and no-download/no-load planning.
- Added audited Qwen3 8B and 14B catalog entries, managed Codex routing instructions, and isolated coverage for passive planning, contention-safe execution, context caps, digest/Q4 rejection, pressure refusal, idempotent preferences, and MCP contracts.

## 0.1.0-beta.4 - 2026-07-14

- Verify every production Ollama TCP peer as a current-user, validly signed `Ollama Inc.` process before sending any HTTP bytes or review prompt.
- Resolve `nvidia-smi.exe` only from trusted absolute Windows/NVIDIA installation paths and never through the current directory or `PATH`.
- Add isolated regression coverage proving an arbitrary loopback peer receives zero HTTP bytes and inert executable-search fixtures are never launched.

## 0.1.0-beta.3 - 2026-07-14

- Added exact live ownership validation before every managed control, model change, model move, inference test, repair, and runtime cleanup operation.
- Made external unmarked reviewers an explicitly unverified, read-only status mode; managed-only controls are disabled and no invocation guidance is installed for an arbitrary external server.
- Added passive non-loopback listener warnings and blocked enable/resume when any Ollama listener is exposed beyond loopback.
- Detects existing user-owned Ollama autostart sources, does not create a duplicate helper Run entry, and reports unverified external sources without certifying them.
- Structurally binds managed ownership to the exact reviewer table inside the unique marker span and fails closed on displaced markers.
- Guards every mutating install route against a different recorded Codex home before context or protected-file writes, and makes final config/state enablement transactional.
- Rechecks ownership around inventory, pull, validation generation, runtime inspection, unload, model-move activation, and rollback boundaries.
- Requires the exact canonical helper Run command for startup certification; augmented or chained commands remain external and unverified.
- Deletes quarantined model files only after individual revalidation and preserves late writer content instead of recursively deleting a changed tree.
- Preserves drifted protected files for reviewed manual recovery instead of rewriting or surgically uninstalling an ownership contract that no longer matches.
- Opens the required model-acquisition page automatically after the installer's deferred bootstrap and highlights required setup headings in orange.
- Added the validated friend installer bundle with a clearly named root installer, exact checksum command, unsigned-build disclosure, beginner install/use guide, and sanitized Codex handoff.
- Publishes the friend bundle as a checksummed, attested prerelease asset and enforces source, assembly, installer, notice, bundle, and PE version consistency.

## 0.1.0-beta.2 - 2026-07-13

- Rebuilt the Windows setup wizard and Control Center with a modern dark AI-inspired interface, stronger action hierarchy, plain-language status, and contextual primary actions.
- Applied a matching native dark installer style and made interactive installation automatically discover the current Codex home and perform a protected, disabled, no-model bootstrap.
- Added hover help and accessibility descriptions for every action button.
- Added first-run model discovery, a model-folder browser, an explicit no-download setup-later path, and a separate model download/validation confirmation with expected size.
- Clarified pause/resume versus persistent enable/disable behavior and made existing unmanaged reviewer preservation a friendly protected state.
- Preserved custom state and Codex-home routing across interactive reinstalls and rejected repair drift before any protected-file or install-context mutation.
- Bound guided acquisition consent to the exact selected model, disclosed possible same-model inventory repair/download, and disabled unapproved automatic fallback downloads.
- Serialized Control Center actions and hardened build scripts against .NET first-run certificate generation and telemetry side effects.

## 0.1.0-beta.1 - 2026-07-13

- Initial independent community beta for Windows x64.
- Added dynamic hardware/storage detection and versioned model catalog.
- Added bounded local stdio MCP health/review tools using ordinary Ollama generation.
- Added safe Codex TOML and AGENTS override merging with backup/rollback.
- Added per-user idempotent Ollama startup, trusted executable-path process handling, exact model path/tag/digest verification, and fail-closed loopback enforcement.
- Added Control Center, CLI, pause/resume/release, repair/update/model move/uninstall operations.
- Added self-contained packaging, Inno Setup source, tests, CI/security/release workflows, checksums, SBOM, and attestation support.
- Added protected-tag/environment release gating, bounded downloads, race-safe model moves, and byte-preserving malformed-file uninstall recovery.
- Added preservation-first coexistence for existing unmarked `local_gpu_reviewer` integrations, with no runtime takeover or duplicate TOML/AGENTS sections.
- Split automatic local GPU guidance from an explicit opt-in sanitized reliability baseline with diff preview, distinct managed markers, backups, idempotent upgrades, and surgical rollback.

The beta installer is not Authenticode-signed and may trigger SmartScreen.
