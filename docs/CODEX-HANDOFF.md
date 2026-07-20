# Codex post-install handoff

This is a sanitized, reusable handoff for configuring and verifying Codex GPU Thalen Helper on the computer where it is installed. It contains no machine-specific paths, credentials, model choices, or private Codex instructions.

## Easiest way to use this

From the friend ZIP, drag `3 - CODEX HANDOFF.md` into a new Codex task. After installation, open **Start > Codex GPU Thalen Helper > Codex setup handoff**, press **Ctrl+A**, then **Ctrl+C**, and paste the whole document into a new Codex task. Add this one sentence:

```text
Follow the attached or pasted CODEX-HANDOFF.md for this computer. I am a beginner, so do the safe technical work with your available tools, explain choices in plain language, and ask me only for decisions or Windows clicks you cannot safely perform yourself.

Start with read-only discovery and passive status checks. Do not directly rewrite my Codex files, download or run a model, change Ollama startup, or repair anything until you show the exact detected state and the helper's proposed action. Use the helper's managed commands and Control Center instead of hand-editing protected files. Preserve any existing unmarked local_gpu_reviewer integration.

Assume this may be a modest computer. Prefer the smallest hardware-safe supported model, low-impact mode, immediate unloading, and skipping review under memory or GPU pressure. Do not choose a larger model merely because it may be more capable.
```

Codex should locate the installed application from the packaged documentation and current Windows state. If it cannot, the user can open **Start > Codex GPU Thalen Helper > Codex setup handoff**; the normal installed copy is inside the application's `docs` directory.

## Goal

Finish or verify a safe, working `local_gpu_reviewer` integration for the current Windows user while preserving all unrelated Codex, Ollama, and LM Studio configuration. Codex should carry out the technical steps; the user should not need to understand PowerShell, TOML, MCP, model tags, GGUF files, or GPU terminology.

## Beginner interaction rules

- Do not hand the user a list of terminal commands and call that completion. Run supported commands with your own shell tools when available. If a GUI-only action is required, name the exact button and wait for the user to confirm it finished.
- Ask one short decision at a time. State the recommended choice first and explain its effect in one sentence.
- Translate status terms into plain language. For example, explain that **No model loaded** usually means GPU memory is safely free.
- Never ask the user to open or edit TOML, JSON, registry, or managed instruction blocks manually.
- When consent is needed, name the exact model, approximate download size, destination, whether inference will run, and whether a Codex restart is needed.
- If the computer cannot safely run any supported local model, say so plainly and leave the helper installed but disabled. Do not force CPU-only use or a marginal model just to finish the checklist.

## Context Codex must establish

1. Locate the installed application without searching unrelated files or drives. If this document has an accessible path, use its parent application directory. Otherwise check the current user's standard `%LOCALAPPDATA%\Programs\Codex GPU Thalen Helper` location. Confirm that the directory contains `thalen-helper.exe`, `local-gpu-reviewer.exe`, `README.md`, `docs`, and `templates`.
2. Read these packaged, public files before proposing changes:
   - `README.md`
   - `docs/friend-install-and-use-guide.md`
   - `docs/configuration-merging.md`
   - `docs/model-selection.md`
   - `docs/task-aware-routing.md`
   - `docs/privacy-and-security.md`
   - `docs/troubleshooting.md`
   - `docs/uninstall.md`
3. Resolve the current user's Codex home from `CODEX_HOME`; only when it is unset, use the current user's `.codex` directory.
4. Run only passive helper diagnostics first from the installed application directory:

   ```powershell
   .\thalen-helper.exe status
   .\thalen-helper.exe doctor
   .\thalen-helper.exe ollama verify
   .\thalen-helper.exe model routing status
   ```

5. Report whether the integration is helper-owned, external/unmarked, incomplete, disabled, paused, or ready. Treat **No model loaded** as the normal safe idle state, not as evidence that setup failed.
6. Use the helper's current hardware detection and packaged model catalog; do not guess suitability from a model's name or from remembered requirements.

## Constraints

- Never replace `config.toml` or `AGENTS.override.md` wholesale. Preserve settings, comments, unknown keys, MCP servers, instructions, and unrelated content.
- Never create a second `mcp_servers.local_gpu_reviewer` table or duplicate a managed instruction section.
- Preserve an existing unmarked `local_gpu_reviewer` integration by default. Do not claim control of it, repair it, test it, or add helper invocation guidance unless the user explicitly chooses a supported migration later.
- Use the helper's backup, reviewed-choice/diff, managed-marker, idempotency, rollback, repair, and surgical-uninstall paths. Do not hand-edit protected Codex files as a shortcut.
- Do not display or copy secrets, Codex authentication, private instructions, prompts, responses, backups, or unrelated configuration into logs or chat.
- Keep every provider loopback-only: Ollama on `127.0.0.1:11434` and LM Studio on `127.0.0.1:1234`. Treat a provider listener reachable beyond loopback as unsafe and do not enable that route while it exists.
- Do not download, pull, load, validate, move, or delete a model without the user's explicit model and storage choice. Passive status and installation must not run inference.
- For modest hardware, prefer the smallest model that the current catalog marks safe for the detected dedicated VRAM and system memory. Keep shared GPU memory separate from dedicated VRAM, never use a larger model based on shared memory, and do not enable CPU-only fallback without explicit informed consent.
- Keep local review single-flight. Do not bypass the helper's lock, queue-or-skip behavior, pressure refusal, workload guard, or idle unloading.
- Prefer [automatic task-aware routing](task-aware-routing.md) after at least one supported provider model is validated. Before each non-trivial local review, use passive `local_gpu_plan`, then announce the returned provider, actual planned model, and bounded purpose before calling `local_gpu_review` with the same applicable task kind, effort, context, and GPU-workload fields. Pass an explicit task kind when known. With Auto, planning deterministically checks its assignment, while review checks optional `focus` before `assignment`; both then use the existing size fallback, and neither routing step is model inference. Planning must not download, load, validate, or run a model.
- Supply only the bounded context needed for the review. Label relative file, symbol, diff-hunk, test, and log locations; mark truncation; distinguish source excerpts from command/test provenance; and never include secrets or unrelated private material. The reviewer still has no repository, filesystem, shell, or Git access.
- Treat additive `structuredFindings` as shape-validated advisory claims, never confirmed facts. Independently check their supplied-text evidence, location, impact, verification step, and false-positive condition. Use `structuredFindingsStatus` to distinguish valid, partially rejected, malformed, and not-run output; raw `findings` remains available for compatibility, and an empty array is not proof of no issues.
- Treat providers differently where their trust boundaries differ. Ollama models must pass the [exact installed-digest checks](model-selection.md#ollama-acquisition-and-runtime-validation). An LM Studio model is eligible only through the [current catalog-audited registration flow](model-selection.md#lm-studio-registration), which currently supports the existing Qwythos GGUF. Do not claim arbitrary GGUF support, and do not trust a legacy beta.11 registration until it is explicitly revalidated.
- Do not interrupt Codex, Expo, Android, iPhone, emulator, graphics, build, or device-testing processes. Prefer low-impact mode during GPU-intensive work.
- Default to low-impact mode on, keep-warm off, queue-or-skip set to skip, and immediate unloading after review on entry-level or uncertain hardware. Increase resource use only after fresh measurements and explicit consent.
- If automatic Ollama startup was declined, leave it declined and explain that Ollama must be started manually after sign-in. Do not create a startup entry without consent.
- If safe semantic merging or ownership verification fails, leave protected files untouched and explain the exact blocker.

## Implementation sequence

1. Present a short plain-language summary of the passive status results and the exact installation, Codex-home, model-store, selected-model, endpoint, startup, and ownership state without exposing file contents or secret values. Put technical details after the recommendation, not before it.
2. If setup is incomplete, offer these mutually exclusive paths:
   - point the helper to an existing Ollama model store and selected installed model;
   - explicitly approve downloading the helper's smallest safe current recommendation into one confirmed fixed local directory;
   - register the exact existing Qwythos GGUF already indexed by the signed current-user LM Studio inventory, with no download, copy, move, or substitution; or
   - leave model setup deferred with no download or inference.
3. Recommend the safest path. On an entry-level or uncertain GPU, recommend the smallest supported model and low-impact settings even when a larger model might technically fit. For an Ollama acquisition, let the user choose the storage folder and show its drive type, current free space, the exact model's approximate size, temporary overhead, and required safety reserve before asking for consent. Add additional eligible models only through a separate named confirmation; do not silently batch downloads.
4. Before a helper-owned repair or explicit migration, run `thalen-helper repair --dry-run --diff-out <private-local-file>` and add `--migrate-existing` only when the user explicitly chose to replace a preserved external reviewer. Keep the full diff local and private, summarize the expected managed-file effects in plain language, and apply only with all four source/planned SHA-256 values returned by that dry-run. Use the Control Center's separate before/after preview for the optional reliability baseline.
5. Apply only the chosen managed action. External ownership remains preserved unless the user explicitly approved the reviewed `--migrate-existing` plan. A prior managed-state record paired with exactly one valid unmarked reviewer can use that same four-hash migration flow; never edit markers or state by hand. If the helper reports any other ambiguous TOML ownership, drift, unsafe network exposure, pressure, or an unverifiable startup owner, stop and report it instead of bypassing the guard.
6. Explicitly validate each installed audited model intended for the automatic pool, one at a time, without substituting a replacement; stop on pressure or release failure. For Ollama, do not pull anything unless the user separately approved that named model and destination after seeing free space and the required reserve. For LM Studio, register only the current catalog-audited existing Qwythos file. Require the signed current-user `lms` inventory, exact catalog-relative path and size, stable regular-file identity, full SHA-256, pinned models-root/path lease, verified loopback REST instance, exact-instance unload, and post-unload CLI absence. A beta.11 record must go through this explicit revalidation before routing. Then turn on automatic model routing and low-impact mode, and leave keep-warm off for modest or uncertain hardware. Automatic routing selects dynamically only from exact-identity models that passed this installation's provider-specific validation and requests immediate unload for every response. Confirm no helper-created model is resident before asking the user to restart Codex.
7. Restart every Codex window only after a successful helper-owned integration change. A Codex restart is required for MCP discovery; restarting the Control Center alone is insufficient. Ask the user to close and reopen Codex only when this point is reached.
8. After the fresh Codex restart, re-run passive status. Run **Test local review** only with explicit consent because it performs a small local inference through the selected provider; verify the exact helper-created instance is unloaded afterward.
9. Provide the backup/restore information and a concise post-change diff summary. Run the same passive checks a second time to confirm the operation was idempotent and did not append duplicates.

## Done

Do not say setup is complete unless the applicable items are verified:

- protected Codex files were preserved except for the expected marked helper-owned additions;
- a second run creates no duplicate TOML table or instruction section;
- every configured provider endpoint responds only on loopback, and neither port 11434 nor 1234 is exposed beyond loopback;
- when Ollama is configured, the managed MCP entry whitelists only `OLLAMA_MODELS`, and its forwarded value resolves to the confirmed model store;
- each routed model remains present in its provider inventory and still matches its current provider-specific validation evidence;
- an LM Studio route, when configured, maps the exact catalog-audited Qwythos key to the exact existing GGUF and no legacy beta.11 record is treated as current validation;
- automatic startup is helper-owned and verified, or manual startup is clearly reported as the user's choice;
- the MCP reviewer appears after a fresh Codex restart when the integration is helper-owned and enabled;
- `local_gpu_health`, `local_gpu_plan`, and `local_gpu_review` appear after restart, and a passive plan reports the task kind, effort, model, context cap, and no inference;
- a consented bounded review preserves raw `findings`, returns only shape-valid records in additive `structuredFindings`, leaves `confirmedObservations` empty, and is never reported as independently verified until Codex checks the cited supplied-text evidence against an authoritative source;
- no helper-created model is left loaded after a consented validation when low-impact mode requires unloading; for LM Studio, both REST inventory and the signed `lms ps` inventory must show the exact instance absent;
- restore instructions and any remaining manual step are reported honestly.

An external/unmarked reviewer may remain preserved and operational, but the packaged helper must label it external and must not claim that the checklist above was verified or controlled on its behalf.

For the residual LM Studio same-key REST-load race and its fail-closed mitigations, read [Provider boundary](task-aware-routing.md#provider-boundary). Do not manually load the same LM Studio key while registration or review is in progress.
