# Codex GPU Thalen Helper v0.1.0-beta.24

This unsigned prerelease fixes a second public-install failure caused when another Codex session silently changed one letter while requesting the long GitHub owner, even though the pasted link appeared correct.

## Stable public install bootstrap

- The paste prompt now begins at `https://thalenai.com/install` and reads `https://thalenai.com/install.json` as machine-readable data.
- The manifest returns short `thalenai.com` links for the installer, checksums, signing disclosure, friend bundle, tagged install guide, Codex handoff, release page, and source repository.
- Codex follows those links and their redirects instead of retyping, reconstructing, autocorrecting, or substituting the GitHub owner, tag, filenames, or URLs.
- The manifest pins the exact release, installer filename, installer SHA-256, and official repository identity. A public install does not require GitHub sign-in.
- Packaging tests prevent a friend bundle from omitting this stable recovery contract.

GitHub Releases remains the final installer source. SHA-256 verification and GitHub artifact attestation remain mandatory, and Codex must still ask immediately before running the unsigned installer. Application and reviewer behavior are unchanged from beta.23. Windows SmartScreen may warn about an unknown publisher.
