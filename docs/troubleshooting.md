# Troubleshooting

## Start with passive checks

```powershell
thalen-helper doctor
thalen-helper ollama verify
```

These checks call inventory/runtime endpoints only and do not run model inference.

## Setup finished but local review is not ready

The guided setup defaults to **Install the helper now and finish model setup later**. That path intentionally downloads, registers, loads, and runs no model, so the dashboard reports that model setup is still required. Open model setup and choose one path: select an Ollama storage folder and existing or hardware-compatible model, register the exact existing catalog-audited Qwythos GGUF already indexed by LM Studio, or keep setup deferred.

Before any Ollama download, verify that the wizard shows the intended destination, current free space, approximate model size, temporary overhead, and required reserve. If a model is unavailable, read its hardware-fit explanation; do not work around the conservative GPU/RAM check by choosing a larger model manually.

Hover over any Control Center button for a plain-language explanation before using it. **Pause** is temporary and keeps the MCP entry configured; **Disable** persistently turns off the helper-owned entry and can require a Codex restart. **Release GPU** requests cancellation and waits for zero-keep-alive release. It never force-unloads a mutable model tag and reports `GPU_RELEASE_UNCONFIRMED` when release cannot be proven.

## The Control Center says no model is loaded

This is normally the safe idle state. Low-impact mode requests immediate release, so Ollama unloads the tracked model and LM Studio unloads the exact helper-created instance after each bounded review. A model being installed, registered, or selected is different from a model being loaded in GPU memory. Use the explicitly confirmed **Test local review** action only when you want to run inference; passive status never loads a model.

During a Codex-started review, beta.21 and later show **Loading**, **Review active**, **Releasing**, or **Check status** with the routed provider and model. That short-lived display signal is intentionally not proof of GPU residency or model ownership. Cleanup controls still require the separate exact ownership tracker, so a stale or forged status display can never authorize unloading a user-owned model. If **Check status** remains visible for more than two minutes, refresh the dashboard and use passive diagnostics; do not manually delete state while a provider may still be working.

Beta.22 and later let a cold local-model load use the helper's full explicit five-minute budget. Earlier builds could be cancelled near 100 seconds by .NET's separate default HTTP timeout even though the provider load was still making progress. The per-operation limits remain bounded; this change does not make provider calls unbounded.

## Review ran but structured findings are empty

Check `modelRan`, `errorCode`, `structuredFindingsStatus`, and the original `findings` text together. `parsed` distinguishes a valid empty result from `malformed` prose/truncated/invalid JSON; `parsed_with_ignored_items` means at least one record was omitted or the 20-item cap applied. The helper preserves bounded original text for compatibility and never turns parsed records into confirmed observations. Do not report the review as clean from the empty array alone. Primary Codex should independently inspect any useful raw claim and may request another explicitly bounded review only when local inference is still appropriate and authorized.

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

`OLLAMA_RESTART_REQUIRED` during setup, repair, move, activation, or recovery means `OLLAMA_MODELS` must change while a shared Ollama process is still running. The helper deliberately does not stop it or unload its models. Close Ollama normally, confirm no Ollama process remains, and retry the same command; do not kill an active generation.

## LM Studio model is not eligible

LM Studio registration supports only the exact existing Qwythos GGUF listed in the bundled audited catalog. The helper does not download or import GGUF files and does not accept a different file merely because LM Studio can load it.

- `LMSTUDIO_CLI_UNAVAILABLE` or `LMSTUDIO_CLI_UNTRUSTED` means the canonical current-user `lms` CLI is missing or its signature could not be trusted. Repair or update LM Studio from its official source; do not point the helper at another executable.
- `LMSTUDIO_MODEL_BINDING_MISMATCH`, `MODEL_FILE_CATALOG_BINDING_MISMATCH`, or `MODEL_DIGEST_MISMATCH` means the key, catalog-relative path, size, regular-file identity, or full SHA-256 did not match the audited entry. Leave the file untouched and select the exact catalog file.
- `LMSTUDIO_MODEL_NAMESPACE_UNSAFE` means the LM Studio models root or its local junction could not be pinned safely. Restore a stable current-user models root and retry; do not replace or retarget it during validation.
- `FOREIGN_MODEL_LOADED` means LM Studio already has a user-owned model instance. The helper leaves it loaded and does not replace it. Unload it yourself when its work is finished, then retry.
- `LMSTUDIO_LOADED_FILE_MISMATCH` or `MODEL_RESPONSE_IDENTITY_MISMATCH` means the loaded instance was not proven to be the exact audited file. The helper refuses generation and unloads only an instance it can prove it created.
- `LMSTUDIO_UNLOAD_UNCONFIRMED` or `GPU_RELEASE_FAILED` means REST inventory and signed `lms ps` did not both prove the exact helper-created instance absent. Keep routing disabled and resolve the LM Studio state before retrying.

A registration created by beta.11 lacks the current signed-CLI and pinned-path evidence. Select the same existing Qwythos file and explicitly revalidate it; do not edit the registration JSON. Do not manually load the same Qwythos key in another LM Studio client while registration or review is running because the REST load boundary has a small documented same-key race window.

## Network exposure warning

Stop the affected provider immediately if a network-exposure warning appears. For `OLLAMA_NETWORK_EXPOSURE` or **EXTERNAL RISK**, remove any wildcard/LAN `OLLAMA_HOST` setting and firewall forwarding, set the user host to `127.0.0.1:11434`, restart Ollama, and rerun verification. LM Studio must use its unauthenticated HTTP loopback endpoint at `127.0.0.1:1234`; do not bind it to a LAN address or add port forwarding. The affected route remains disabled until loopback-only status passes. An external reviewer remains outside packaged control even after its listener is corrected.

## GPU is needed by another application

Use `thalen-helper pause` for immediate call blocking/cancellation, or `thalen-helper release-gpu` to request and observe release without persistent disable. Leave keep-warm off and low-impact on.

## Model validation fails

Review the specific error code. Update the GPU driver or chosen provider only through its normal trusted updater, close heavy GPU workloads, verify free disk/RAM, then retest. Guided setup does not switch to a different fallback model; select and confirm another supported route if needed. It never deletes pre-existing Ollama models or LM Studio GGUF files.

## Safe diagnostics

```powershell
thalen-helper diagnostics export .\helper-diagnostics.json
```

Review the JSON before sharing. It is designed to exclude prompts, responses, usernames, hostnames, serials, and credentials.
