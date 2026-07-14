# Release process

Releases are authorized only for `rennavationstudios-bit/codex-gpu-thalen-helper`.

1. Reconcile the full task ledger and changelog.
2. Run `.\eng\release-audit.ps1`; fix every validated failure and rerun.
3. Confirm repository root and inspect every tracked file.
4. Immediately before any create/push/tag/release action, run `gh auth status` and confirm the active login is exactly `rennavationstudios-bit`. Stop on any mismatch.
5. Confirm the exact GitHub repository is absent for first publication or is this project for later releases. Never overwrite an unrelated repository.
6. Before the first tag, create the protected `release` environment and an active repository tag ruleset for `v*` that restricts tag creation, update, deletion, and bypass to the exact release authority. Verify both controls through GitHub before continuing.
7. Push the reviewed default branch and wait for CI/security workflows.
8. Create the version tag only from the reviewed `main` commit. The workflow accepts only `v<major>.<minor>.<patch>-beta.<number>` and independently proves the event SHA, peeled tag SHA, checked-out SHA, and `main` ancestry are identical.
9. Let the release workflow rebuild from the tag with locked dependencies.
10. Publish installer, portable ZIP, friend installer ZIP with the root Codex handoff, SHA-256 file, signing disclosure, SPDX SBOM, and release notes.
11. Create GitHub artifact attestations and verify them from a clean download.
12. Mark unsigned builds as prereleases and disclose SmartScreen/unknown publisher prominently.
13. Verify the public repository/release URLs, direct assets/checksums, CI, security features, and attestation.

Never store signing material in GitHub source or expose secrets to pull-request workflows. A future signed release requires an approved certificate and external signing process.

The release notes must state independent-community status, supported architecture, unsigned/signed status, checksum, model/storage behavior, startup behavior, security boundaries, test evidence, and known physical-hardware limitations.
