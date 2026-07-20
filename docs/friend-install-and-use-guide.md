# Friend install and use guide

This bundle is designed for a Windows user who has never configured Codex, Ollama, LM Studio, or a local model integration before.

## Install

The simplest option is to open `0 - PASTE THIS INTO CODEX.md`, copy its prompt into a new Codex task, and let Codex select the highest valid semantic prerelease from the exact official repository, verify it, install it, and validate setup. Codex must still ask immediately before running the unsigned installer and must ask separately before each model download, load, validation, move, or delete action.

1. Extract the entire ZIP to a normal local folder. Do not run the installer from inside the ZIP preview.
2. Open `1 - START HERE.txt` and verify the installer SHA-256 as shown there.
3. Double-click `2 - INSTALL Codex GPU Thalen Helper.exe`.
4. This is an unsigned beta. Windows SmartScreen may say **Unknown publisher**. Continue only when the SHA-256 matches `INSTALLER-SHA256.txt` and the bundle came from someone you trust.
5. Read the installer choices. Automatic Ollama startup is recommended. If another per-user Ollama startup entry already exists, the helper preserves it and does not add a duplicate, but labels it unverified until you review or replace that launcher. You can instead choose manual startup.
6. Setup finds the current user's Codex home, creates timestamped backups of existing `config.toml` and `AGENTS.override.md`, and adds only marked managed content. It never replaces either file.
7. The base install does not download or load a model. When the Control Center opens, choose one path:
   - choose where Ollama models should be stored and use an existing model there;
   - choose a hardware-compatible Ollama model and explicitly approve its download and bounded validation;
   - register the exact existing Qwythos GGUF already indexed by LM Studio, with no download; or
   - finish model setup later.
8. Before an Ollama download, the wizard shows the model size, selected drive, current free space, temporary overhead, and required safety reserve. It lists only conservative GPU fits, plus any CPU path you explicitly enabled. It never silently chooses a different model. Add more eligible models later one confirmed model at a time.
9. Restart every Codex window after helper-owned setup so Codex discovers the MCP tools.

## Let Codex help safely

For a complete install, open `0 - PASTE THIS INTO CODEX.md` and paste its copy-ready prompt into a new Codex task. For post-install setup or repair, drag `3 - CODEX HANDOFF.md` into a new task. You can also open **Start > Codex GPU Thalen Helper > Codex setup handoff**, press **Ctrl+A**, then **Ctrl+C**, and paste the whole document into Codex. These handoffs tell Codex which public source and packaged files to trust, which passive checks to run first, which protected-file and privacy rules to preserve, when consent is required, and exactly what must be verified before setup can be called complete.

The handoff is generic and contains no sender-specific paths, settings, model choices, credentials, or private instructions. It directs Codex to discover the current computer's state instead of assuming that another person's configuration applies.

You do not need to know PowerShell, TOML, MCP, model tags, or GPU specifications. Codex is instructed to do supported technical steps itself, explain simple decisions one at a time, and ask you only for consent or Windows clicks it cannot perform. On a modest computer it defaults to the smallest safe current model recommendation, low-impact mode, no keep-warm behavior, and immediate unloading; if no supported model fits safely, it leaves local review disabled instead of forcing it.

If setup finds an existing unmarked `local_gpu_reviewer`, it preserves that integration byte-for-byte. It does not test or take control of it. The Control Center labels it **EXTERNAL**, disables managed-only controls, and does not add invocation guidance for that server.

## Understand the status

- **READY**: the helper-owned integration passed passive safety checks. The model still remains unloaded until a review is requested.
- **PAUSED**: new local reviews are temporarily blocked; the Codex MCP entry remains configured.
- **OFF**: the helper-owned MCP entry is persistently disabled.
- **SETUP**: choose or validate a model before local review can run.
- **EXTERNAL**: Codex may have another reviewer, but this app does not control or verify it.
- **EXTERNAL RISK / NETWORK EXPOSED**: an Ollama listener is reachable beyond loopback. Stop Ollama and correct that external startup or host setting before local review.
- **OLLAMA PEER NOT VERIFIED**: something owns the local Ollama port but is not the expected current-user, validly signed official Ollama process. The helper sends it no review prompt. Close the unknown listener and repair or install official Ollama for Windows.
- **EXTERNAL AUTOSTART UNVERIFIED**: another Run/Startup artifact was preserved to avoid duplication, but its target and next-login environment were not certified. Review/remove it or use manual startup before enabling managed review.
- **No model loaded**: normally the safe idle state. Installed models stay on disk while GPU memory is released.
- **Existing reviewer preserved**: setup found a reviewer it does not own, so it did not replace or control it. Ask Codex to use the included handoff. Codex can create a private dry-run diff, explain it simply, and migrate only after you approve the exact reviewed change.

Passive status never runs a model. **Test local review** is the explicit action that runs a small validation inference and unloads afterward.

## Controls

- **Pause reviews** temporarily blocks helper reviews, requests cancellation, and waits for a tracked zero-keep-alive review to release. It never force-unloads a model tag.
- **Resume reviews** repeats safety verification and allows reviews without preloading the model.
- **Enable integration** persistently enables the helper-owned Codex MCP entry after safety checks.
- **Disable integration** persistently disables that entry; restart Codex if its tools remain visible in the current session.
- **Release GPU** requests cancellation and waits for a tracked zero-keep-alive review to release without blocking future reviews. It reports an unconfirmed release rather than force-unloading a mutable model tag.
- **Low impact** unloads immediately after each response and is recommended during emulator, Expo, graphics, or device work.
- **Keep warm** retains the model for a bounded idle period when hardware headroom permits.
- **Auto model** lets each Codex task passively choose a safe installed, catalog-audited, provider-validated model for its task type, size, and current hardware headroom. An explicit task type wins; otherwise planning checks simple deterministic phrases in its assignment, while review can check a more specific focus first, then falls back to conservative input-size categories. It also uses existing catalog task guidance and preferred hardware tiers, but these are not model-quality guarantees. This can include Ollama models and the currently audited existing LM Studio/Qwythos route. During GPU-intensive work it still chooses the smallest safe option. Turning Auto off pins the validated selected model. Automatic mode unloads after every response and therefore disables **Keep warm**.

The helper does not control Codex or its cloud model. It offers Codex three optional local tools: passive health, passive task/model planning, and one bounded read-only review. `local_gpu_plan` downloads, loads, and runs nothing; it tells Codex which installed model and context would be used before Codex announces and requests a review. The review keeps the model's original bounded text and can also organize up to 20 complete findings into claim, supplied-text location/evidence, confidence, impact, verification, and false-positive fields. That organization checks only the response shape. Codex must still verify every useful claim, and an empty organized list does not by itself prove there were no issues. Codex decides whether to request a tool, and the local result remains advisory.

## Models and automatic startup

Setup never silently downloads a model. A missing Ollama model can be downloaded only after a named model choice, hardware-fit check, storage selection, free-space/reserve check, and confirmation. A user with existing Ollama models can point setup to that model store. The selected tag, digest, model folder, endpoint, and loopback listener must verify before the helper enables that route.

LM Studio is an existing-model path, not a download service. This release supports only the catalog-audited Qwythos GGUF already indexed beneath the current user's LM Studio models root. Registration requires the signed current-user LM Studio inventory to match the exact catalog-relative path and size, then checks the file identity and full SHA-256 before bounded validation through the loopback server. It unloads only the exact instance it created and verifies that instance is gone. Older beta.11 registrations must be explicitly revalidated; other GGUF files are not accepted merely because LM Studio can open them.

Automatic routing is dynamic for this computer's measured dedicated/available VRAM, system RAM, acceleration route, current pressure, and installed audited models that passed this installation's provider-specific exact-identity validation. A missing, old, or failed pass keeps that model out of the automatic pool. Hardware examples in the test suite are boundaries, not preferred devices. Keep both providers loopback-only: Ollama on port 11434 and LM Studio on port 1234.

When automatic Ollama startup is selected, the helper avoids duplicate startup entries and duplicate Ollama processes. When it is declined, the app remains installed but Ollama must be started manually after each sign-in before an Ollama review works. LM Studio remains user-controlled; start its loopback local server before an LM Studio review.

## Help and uninstall

Hover over any Control Center button for a plain-language explanation. For failures, read `docs\TROUBLESHOOTING.md` and run passive checks from a terminal:

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

Uninstall from **Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall**. The uninstaller preserves Codex authentication, unrelated Codex content, pre-existing Ollama, and pre-existing models. Read `docs\UNINSTALL.md` for recovery details.
