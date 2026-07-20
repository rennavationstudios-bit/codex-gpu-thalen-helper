# Codex GPU Thalen Helper v0.1.0-beta.21

This unsigned prerelease completes the live activity-status fix begun in beta.20. It preserves beta.19 task-aware routing and structured advisory results plus beta.20's transparent overflow glyph.

## Review activity that is visible and truthful

- A separate display-only activity record now follows bounded reviews through **Loading**, **Review active**, **Releasing**, and short-lived **Check status** phases.
- The activity begins only after the shared GPU lease and safety checks accept an exact provider/model route. This makes long LM Studio loads visible before the provider returns the instance identity required by strict ownership tracking.
- Ollama and LM Studio use the same observational contract, and the Control Center restores the exact prior passive status after the lifecycle ends.
- Malformed, future, or expired activity records are ignored. Loading/reviewing records expire after ten minutes; releasing/attention records expire after two minutes.

## Trust boundary

Review activity is not model ownership, provider health, or proof of GPU residency. It cannot authorize release, unload, routing, configuration, or any model-control action. The existing `active-routed-model.txt` tracker remains the only cleanup authority, and all existing foreign-model preservation and exact-unload rules remain unchanged.

The activity file contains only schema version, a random operation identifier, normalized provider, validated model key, allowlisted phase, and timestamps. It contains no prompt, response, repository content, path, digest, username, hostname, or machine identifier. Activity I/O is best-effort and cannot block or reroute a review.

## UI and packaging

The compact Advanced control remains a transparent keyboard-accessible ellipsis glyph with no painted pill or rectangular mask. The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.

The isolated release suite passes **401 tests** without real model inference, including cancellation during provider generation and post-load verification, ambiguous-load, verified-cleanup, release-failure, unknown-phase, and control-authority boundaries. Temporary uninstall reports also use collision-safe non-identifying filenames so simultaneous test or removal paths cannot overwrite one another; protected-file merging and model preservation are unchanged.
