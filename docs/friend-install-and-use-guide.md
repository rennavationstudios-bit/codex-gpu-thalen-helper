# Friend install and use guide

This bundle is designed for a Windows user who has never configured Codex, Ollama, or a local model integration before.

## Install

1. Extract the entire ZIP to a normal local folder. Do not run the installer from inside the ZIP preview.
2. Open `1 - START HERE.txt` and verify the installer SHA-256 as shown there.
3. Double-click `2 - INSTALL Codex GPU Thalen Helper.exe`.
4. This is an unsigned beta. Windows SmartScreen may say **Unknown publisher**. Continue only when the SHA-256 matches `INSTALLER-SHA256.txt` and the bundle came from someone you trust.
5. Read the installer choices. Automatic Ollama startup is recommended. If another per-user Ollama startup entry already exists, the helper preserves it and does not add a duplicate, but labels it unverified until you review or replace that launcher. You can instead choose manual startup.
6. Setup finds the current user's Codex home, creates timestamped backups of existing `config.toml` and `AGENTS.override.md`, and adds only marked managed content. It never replaces either file.
7. The base install does not download or load a model. When the Control Center opens, choose one path:
   - use an existing Ollama model folder;
   - choose a supported model and explicitly approve its download and bounded validation; or
   - finish model setup later.
8. Restart every Codex window after helper-owned setup so Codex discovers the MCP tools.

## Let Codex help safely

Drag `3 - CODEX HANDOFF.md` from the extracted friend bundle into a new Codex task. Or, after installation, open **Start > Codex GPU Thalen Helper > Codex setup handoff**, press **Ctrl+A**, then **Ctrl+C**, and paste the whole document into Codex. The handoff tells Codex which packaged files to read, which passive checks to run first, which protected-file and privacy rules to preserve, when consent is required, and exactly what must be verified before setup can be called complete.

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

Passive status never runs a model. **Test local review** is the explicit action that runs a small validation inference and unloads afterward.

## Controls

- **Pause reviews** temporarily blocks helper reviews and unloads the selected model.
- **Resume reviews** repeats safety verification and allows reviews without preloading the model.
- **Enable integration** persistently enables the helper-owned Codex MCP entry after safety checks.
- **Disable integration** persistently disables that entry; restart Codex if its tools remain visible in the current session.
- **Release GPU** unloads the model without blocking a future review.
- **Low impact** unloads immediately after each response and is recommended during emulator, Expo, graphics, or device work.
- **Keep warm** retains the model for a bounded idle period when hardware headroom permits.
- **Auto model** lets each Codex task passively choose the safest installed audited Q4 model for its task type, size, and current hardware headroom. Turning it off pins the validated selected model. Automatic mode unloads after every response and therefore disables **Keep warm**.

The helper does not control Codex or its cloud model. It offers Codex three optional local tools: passive health, passive task/model planning, and one bounded read-only review. `local_gpu_plan` downloads, loads, and runs nothing; it tells Codex which installed model and context would be used before Codex announces and requests a review. Codex decides whether to request a tool, and the local result remains advisory.

## Models and automatic startup

Setup never silently downloads a model. A missing model can be downloaded only after a named model choice and confirmation. A user with existing models can point setup to that Ollama model store. The selected tag, digest, model folder, endpoint, and loopback listener must verify before the helper enables review.

Automatic routing is dynamic for this computer's measured dedicated/available VRAM, system RAM, acceleration route, current pressure, and installed audited Ollama models. Hardware examples in the test suite are boundaries, not preferred devices. Models that exist only in LM Studio, including Qwythos or Qwen3.6 files, are not silently used by the current Ollama reviewer; a future provider adapter must first meet the same loopback, process-trust, locking, pressure, and unload rules.

When automatic startup is selected, the helper avoids duplicate startup entries and duplicate Ollama processes. When it is declined, the app remains installed but Ollama must be started manually after each sign-in before local review works.

## Help and uninstall

Hover over any Control Center button for a plain-language explanation. For failures, read `docs\TROUBLESHOOTING.md` and run passive checks from a terminal:

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

Uninstall from **Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall**. The uninstaller preserves Codex authentication, unrelated Codex content, pre-existing Ollama, and pre-existing models. Read `docs\UNINSTALL.md` for recovery details.
