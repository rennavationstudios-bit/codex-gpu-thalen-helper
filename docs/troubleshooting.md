# Troubleshooting

## Start with passive checks

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

These checks call inventory/runtime endpoints only and do not run model inference.

## MCP tools are missing after install

Restart every Codex process after setup. Confirm `config.toml` contains one marked helper block and the installed executable path exists. Run `thalen-helper repair`, then restart Codex again. An unmanaged table with the same integration name is intentionally not overwritten.

## Ollama does not start after sign-in

Run `thalen-helper ollama autostart` and inspect the returned code. `OLLAMA_PROCESS_UNHEALTHY` means an Ollama process exists without a responsive endpoint; the helper did not create a duplicate. Close/restart Ollama or run repair. If automatic startup was declined, manually start Ollama after each sign-in.

## Model path is not verified

`MODEL_PATH_NOT_CONFIGURED` means the current user's `OLLAMA_MODELS` differs from product state. `MODEL_NOT_IN_CONFIGURED_PATH` means the selected manifest was not found there. Run repair to safely restart Ollama with the persisted path, then verify. Do not manually copy only blobs; use `models move` for a verified full move.

## Network exposure warning

Stop Ollama immediately if `OLLAMA_NETWORK_EXPOSURE` appears. Remove any wildcard/LAN `OLLAMA_HOST` setting and firewall forwarding, set the user host to `127.0.0.1:11434`, restart Ollama, and rerun verification. The helper remains disabled until loopback-only status passes.

## GPU is needed by another application

Use `thalen-helper pause` for immediate call blocking/cancellation plus unload, or `thalen-helper release-gpu` to unload without persistent disable. Leave keep-warm off and low-impact on.

## Model validation fails

Review the specific error code. Update the GPU driver/Ollama, close heavy GPU workloads, verify free disk/RAM, then retest. Setup attempts at most one smaller safe fallback. It never deletes pre-existing models.

## Safe diagnostics

```powershell
thalen-helper diagnostics export .\helper-diagnostics.json
```

Review the JSON before sharing. It is designed to exclude prompts, responses, usernames, hostnames, serials, and credentials.
