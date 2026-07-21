# Install this local Codex MCP with Codex

Codex GPU Thalen Helper is an independent community Windows app that installs an optional, read-only local MCP reviewer for Codex. The MCP cannot install itself before it is registered, so this file gives Codex a safe bootstrap workflow.

## Copy and paste this into Codex

```text
Install Codex GPU Thalen Helper for me through its stable public bootstrap:

https://thalenai.com/install

Read this machine-readable manifest as data:

https://thalenai.com/install.json

Use every URL value returned by that manifest verbatim. Never retype, reconstruct, autocorrect, translate, or substitute a GitHub owner, repository, tag, filename, or URL. Download only through the manifest's short thalenai.com links and follow their redirects. Do not search GitHub and do not require my GitHub account.

Verify the installer SHA-256 against both the manifest and the returned SHA256SUMS.txt. Fetch the manifest's attestation link without authentication; require HTTP 200, the exact installer digest in its final URL, and at least one GitHub attestation record whose `repository_id` matches the manifest. This public GitHub API record is online repository-and-digest evidence, not offline local signature verification. If GitHub CLI is already installed and authenticated, also run the manifest's optional cryptographic verification command; do not require GitHub sign-in or install another developer tool solely for this bootstrap. Read the manifest's install-guide and Codex-handoff links. If any request or proof fails, stop without running anything and report the exact short URL, HTTP status, and final redirected URL.

I am a beginner, so use your available tools to do the safe technical work, explain choices in plain language, and ask me only for decisions or Windows clicks you cannot safely perform yourself.

Use GitHub Releases as the only final installer source. Confirm that each short link redirects to the exact repository and release declared by the manifest. Verify the exact repository owner/name, release checksum, and public GitHub attestation record before running anything. Tell me the exact version, the online-versus-local attestation assurance, and the unsigned SmartScreen warning, then ask immediately before running the downloaded installer. Preserve my existing Codex configuration and instructions, use the helper's protected backup/dry-run/managed merge paths, and never hand-edit or replace those files.

Do not download, load, validate, move, or delete a model without first showing me the exact provider, model, approximate size, storage path, and whether inference will run. Ask immediately before each such model action and wait for my explicit approval. Detect this computer's real hardware and installed models instead of copying another person's choices. For Ollama, use only an existing audited model or a separately approved named download into the verified storage folder. For LM Studio, never install LM Studio or download, copy, move, or substitute a GGUF; use only the helper-managed registration flow for an existing catalog-supported local model that I explicitly select. Prefer low-impact settings on modest hardware. Keep every provider loopback-only and leave the helper disabled if safe setup cannot be proven.

After installation, finish the packaged CODEX-HANDOFF.md checklist, tell me when a full Codex restart is required, verify the MCP after restart, prove the setup is idempotent, and report the backup/restore path plus any remaining manual step. Do not claim completion from installation alone.
```

## What Codex must verify before installation

1. The repository is exactly `rennavationstudios-bit/codex-gpu-thalen-helper`. A fork, look-alike owner, mirror, direct-message attachment, or unrelated repository is not an approved source.
   - Start at `https://thalenai.com/install` and parse `https://thalenai.com/install.json`; this prevents an assistant from retyping or silently autocorrecting the long GitHub owner.
   - Use the returned short `thalenai.com` links verbatim and confirm their final redirects match the manifest repository, release, and filenames.
   - The exact public repository URL is `https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper`.
   - The exact public bootstrap URL is `https://raw.githubusercontent.com/rennavationstudios-bit/codex-gpu-thalen-helper/main/INSTALL-WITH-CODEX.md`.
   - None of these public URLs requires a signed-in GitHub session. If a request fails, report the exact short URL, HTTP status, and final redirected URL instead of guessing a replacement.
2. The chosen release is the highest valid semantic prerelease from that exact repository with `isDraft=false`, `isPrerelease=true`, and the complete required asset set: the Windows installer, `SHA256SUMS.txt`, release notes, and the friend bundle. Do not use `/releases/latest` to resolve it.
3. The downloaded installer SHA-256 exactly matches its line in `SHA256SUMS.txt`.
4. Fetch the manifest's short attestation link without authentication and require all of the following:
   - HTTP 200 from the final `api.github.com` request.
   - The final URL contains the exact `sha256:` digest of the downloaded installer.
   - At least one returned attestation has a `repository_id` equal to the manifest's numeric `repository.id`.
   - The user is told this is an online GitHub API repository-and-digest record, not offline local cryptographic verification.

   If GitHub CLI is already installed **and authenticated**, Codex should additionally run:

   ```powershell
   gh attestation verify .\Codex-GPU-Thalen-Helper-Setup.exe --repo rennavationstudios-bit/codex-gpu-thalen-helper
   ```

   Do not require GitHub sign-in and do not install GitHub CLI solely for this bootstrap. The public attestation-record check above is the supported signed-out path. If an already-available local cryptographic verification is attempted and fails, stop without running the installer.

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
