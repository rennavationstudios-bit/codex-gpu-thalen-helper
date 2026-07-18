# Troubleshooting

## Start with passive checks

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

These checks call inventory/runtime endpoints only and do not run model inference.

## Setup finished but local review is not ready

The guided setup defaults to **Install the helper now and finish model setup later**. That path intentionally downloads and loads no model, so the dashboard reports that model setup is still required. Choose **Choose model** to point to an existing Ollama model folder or explicitly approve a download and bounded validation.

Hover over any Control Center button for a plain-language explanation before using it. **Pause** is temporary and keeps the MCP entry configured; **Disable** persistently turns off the helper-owned entry and can require a Codex restart. **Release GPU** unloads only a currently tracked helper-owned model and never an untracked model merely because its name matches the configured selection.

## The Control Center says no model is loaded

This is normally the safe idle state. Low-impact mode uses `keep_alive=0`, so Ollama unloads the model after each bounded review. A model being installed or selected is different from a model being loaded in GPU memory. Use the explicitly confirmed **Test local review** action only when you want to run inference; passive status never loads a model.

## The reviewer is labeled external

Setup found an unmarked `local_gpu_reviewer` that it does not own. It preserves that entry byte-for-byte and does not test, invoke, pause, unload, reconfigure, or add instructions for it. Managed-only Control Center buttons remain disabled. Run `thalen-helper repair --dry-run --diff-out <private-local-file> --migrate-existing`, review the private diff, then apply only with the four returned source/planned hashes and the same `--migrate-existing` flag. Ambiguous table layouts are intentionally refused. Packaged locking, pressure refusal, startup, routing, and unload controls apply only after that explicit migration succeeds.

## MCP tools are missing after install

Restart every Codex process after setup. Confirm `config.toml` contains one marked helper block and the installed executable path exists. Run `thalen-helper repair`, then restart Codex again. An unmanaged table with the same integration name is intentionally not overwritten.

## Ollama does not start after sign-in

Run `thalen-helper ollama autostart` and inspect the returned code. `OLLAMA_PROCESS_UNHEALTHY` means an Ollama process exists without a responsive endpoint; the helper did not create a duplicate. `EXTERNAL_AUTOSTART_UNVERIFIED` means another Ollama-named Run or Startup-folder artifact was preserved to avoid duplication, but its executable and next-login behavior were not certified. Review or remove that launcher, or choose manual startup; the helper does not report it as configured merely because its name contains Ollama. If automatic startup was declined, manually start Ollama after each sign-in.

`OLLAMA_PEER_IDENTITY_UNVERIFIED` means something is listening on the loopback port but the exact connected process was not a current-user, validly signed `Ollama Inc.` `ollama.exe` or `ollama app.exe`. The helper sends no HTTP request or review prompt to that process. Stop the unknown listener, install or repair the official Ollama for Windows build, and retry. Unsigned/self-built Ollama binaries are intentionally rejected by this release.

## Model path is not verified

`MODEL_PATH_NOT_CONFIGURED` means either the current user's `OLLAMA_MODELS` differs from product state or the managed MCP entry is missing the `env_vars = ["OLLAMA_MODELS"]` forwarding whitelist. Run `repair --dry-run` and inspect the protected-file diff. Apply a changed repair only with all four returned hashes, then fully restart Codex so the stdio child receives the forwarded value. `MODEL_NOT_IN_CONFIGURED_PATH` means the selected manifest was not found there. Do not manually copy only blobs; use `models move` for a verified full move, or `models activate` to verify and select a complete pre-copied store while preserving the original.

`MODEL_STORAGE_TRANSITION_PENDING` means activation stopped after writing its crash-recovery marker. Keep both model directories unchanged and run `thalen-helper models recover --yes`; ordinary repair, enable, and resume remain blocked until recovery verifies and clears the marker.

## Network exposure warning

Stop Ollama immediately if `OLLAMA_NETWORK_EXPOSURE` or **EXTERNAL RISK** appears. Remove any wildcard/LAN `OLLAMA_HOST` setting and firewall forwarding, set the user host to `127.0.0.1:11434`, restart Ollama, and rerun verification. The helper remains disabled until loopback-only status passes. An external reviewer remains outside packaged control even after its listener is corrected.

## GPU is needed by another application

Use `thalen-helper pause` for immediate call blocking/cancellation plus unload, or `thalen-helper release-gpu` to unload without persistent disable. Leave keep-warm off and low-impact on.

## Model validation fails

Review the specific error code. Update the GPU driver/Ollama, close heavy GPU workloads, verify free disk/RAM, then retest. Guided setup does not switch to a different fallback model; select and confirm another supported model if needed. It never deletes pre-existing models.

## Safe diagnostics

```powershell
thalen-helper diagnostics export .\helper-diagnostics.json
```

Review the JSON before sharing. It is designed to exclude prompts, responses, usernames, hostnames, serials, and credentials.
