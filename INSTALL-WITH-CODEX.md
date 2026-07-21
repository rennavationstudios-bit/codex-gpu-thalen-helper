# Install this local Codex MCP with Codex

Codex GPU Thalen Helper is an independent community Windows app that installs an optional, read-only local MCP reviewer for Codex. The MCP cannot install itself before it is registered, so this file gives Codex a safe bootstrap workflow.

## Copy and paste this into Codex

```text
Install the highest valid semantic prerelease of Codex GPU Thalen Helper from this exact public repository:

https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper

Begin by opening that exact repository URL and this exact public bootstrap guide URL without requiring GitHub sign-in:

https://raw.githubusercontent.com/rennavationstudios-bit/codex-gpu-thalen-helper/main/INSTALL-WITH-CODEX.md

Do not construct or guess either URL. Do not treat a 404 from a GitHub app, search connector, or constructed deep link as proof that the repository is private. If a connector fails, retry the exact URLs above with an ordinary unauthenticated HTTPS request or browser. A public installation must not require my GitHub account. Only if an exact URL above also fails should you stop, report that exact URL and HTTP result, and ask me to verify the project address.

Read and follow the bootstrap guide above. After selecting the release tag, replace `{TAG}` in these exact raw URL templates and read both files from that tag:

https://raw.githubusercontent.com/rennavationstudios-bit/codex-gpu-thalen-helper/{TAG}/INSTALL-WITH-CODEX.md
https://raw.githubusercontent.com/rennavationstudios-bit/codex-gpu-thalen-helper/{TAG}/docs/CODEX-HANDOFF.md

I am a beginner, so use your available tools to do the safe technical work, explain choices in plain language, and ask me only for decisions or Windows clicks you cannot safely perform yourself.

Use GitHub Releases as the only installer source. Consider only releases from that exact repository where `isDraft=false` and `isPrerelease=true`; choose the highest valid semantic prerelease that has the complete required asset set. Do not use `/releases/latest`, because GitHub excludes prereleases from that endpoint. Verify the exact repository owner/name, release checksums, and GitHub artifact attestation before running anything. Tell me the exact version and unsigned SmartScreen warning, then ask immediately before running the downloaded installer. Preserve my existing Codex configuration and instructions, use the helper's protected backup/dry-run/managed merge paths, and never hand-edit or replace those files.

Do not download, load, validate, move, or delete a model without first showing me the exact provider, model, approximate size, storage path, and whether inference will run. Ask immediately before each such model action and wait for my explicit approval. Detect this computer's real hardware and installed models instead of copying another person's choices. For Ollama, use only an existing audited model or a separately approved named download into the verified storage folder. For LM Studio, never install LM Studio or download, copy, move, or substitute a GGUF; use only the helper-managed registration flow for an existing catalog-supported local model that I explicitly select. Prefer low-impact settings on modest hardware. Keep every provider loopback-only and leave the helper disabled if safe setup cannot be proven.

After installation, finish the packaged CODEX-HANDOFF.md checklist, tell me when a full Codex restart is required, verify the MCP after restart, prove the setup is idempotent, and report the backup/restore path plus any remaining manual step. Do not claim completion from installation alone.
```

## What Codex must verify before installation

1. The repository is exactly `rennavationstudios-bit/codex-gpu-thalen-helper`. A fork, look-alike owner, mirror, direct-message attachment, or unrelated repository is not an approved source.
   - The exact public repository URL is `https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper`.
   - The exact public bootstrap URL is `https://raw.githubusercontent.com/rennavationstudios-bit/codex-gpu-thalen-helper/main/INSTALL-WITH-CODEX.md`.
   - Neither URL requires a signed-in GitHub session. A connector-specific or guessed-link 404 is not enough to classify the repository as private; retry the exact URL over ordinary unauthenticated HTTPS and report the exact failing URL/status if it still fails.
2. The chosen release is the highest valid semantic prerelease from that exact repository with `isDraft=false`, `isPrerelease=true`, and the complete required asset set: the Windows installer, `SHA256SUMS.txt`, release notes, and the friend bundle. Do not use `/releases/latest` to resolve it.
3. The downloaded installer SHA-256 exactly matches its line in `SHA256SUMS.txt`.
4. This GitHub attestation verification succeeds against the exact repository:

   ```powershell
   gh attestation verify .\Codex-GPU-Thalen-Helper-Setup.exe --repo rennavationstudios-bit/codex-gpu-thalen-helper
   ```

   GitHub stores the attestation in its attestation service; it is not a standalone release asset. If `gh attestation verify` or an equivalent GitHub provenance verification is unavailable or unsuccessful, stop without running the installer.

5. The user is told that the prerelease is not Authenticode-signed and may trigger Windows SmartScreen. Checksum and attestation verification do not remove that warning.
6. Codex asks immediately before running the newly downloaded executable. If provenance, integrity, or the repository identity cannot be verified, it stops without running it.

## Safe completion boundary

- Prefer the published release installer. Do not build and install an arbitrary working tree for an ordinary end user.
- The base installation must not download or load a model.
- Setup offers hardware-compatible Ollama choices and, when supported by that release, a separate existing-model LM Studio registration path. Each model is confirmed and validated one at a time; there is no silent batch acquisition.
- Use the installer, Control Center, and `thalen-helper` managed operations instead of directly rewriting `config.toml`, `AGENTS.override.md`, startup entries, or product state.
- Preserve an existing unmarked `local_gpu_reviewer` by default. Migration requires its own private diff, four hash bindings, and explicit approval.
- Read the installed `docs/CODEX-HANDOFF.md` after setup and complete its passive discovery, model consent, restart, verification, idempotency, and rollback checklist.
- Never send Codex authentication, secrets, private instructions, prompts, responses, or backups to a model or third party.
- If no supported model safely fits the detected hardware, leave the MCP installed but disabled and explain that outcome honestly.

The project does not provide a remote installer MCP, silent elevated installer, browser extension, or public network service. The installed reviewer is local stdio, and its supported provider traffic remains on loopback.
