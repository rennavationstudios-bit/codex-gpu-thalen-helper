# Codex GPU Thalen Helper v0.1.0-beta.23

This unsigned prerelease fixes the public **Paste into Codex** installation handoff after a signed-out Codex session misclassified one bad GitHub deep-link response as a private repository.

## Public installation no longer depends on GitHub sign-in

- The bootstrap now lists the exact public repository and raw guide URLs instead of asking Codex to construct them.
- A GitHub app, connector, search, or guessed deep-link 404 is no longer treated as proof that the repository is private.
- Codex is told to retry the exact listed URLs with ordinary unauthenticated HTTPS and to report the precise failing URL and status if the public endpoint itself fails.
- Exact release-tag raw URL templates identify the install guide and packaged handoff after the highest complete semantic prerelease is selected.
- The friend-bundle build now fails if this public-access recovery contract is missing.

The application and reviewer behavior are unchanged from beta.22. The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
