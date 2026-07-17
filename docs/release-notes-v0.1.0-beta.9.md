# Codex GPU Thalen Helper v0.1.0-beta.9

This prerelease adds a production-safe, non-destructive activation path for a model store that was copied and independently verified outside the Helper.

## Model storage activation

- `thalen-helper models activate <existing-fixed-local-directory> --yes` compares the configured source and destination by relative file and empty-directory paths, lengths, timestamps, ordinary attributes, and SHA-256 before changing state.
- The source and destination trees are read-only throughout activation; the command never copies, renames, quarantines, or deletes either tree.
- Reparse points in either tree or its path ancestry are refused, and the destination must resolve through ready fixed local storage.
- Any loaded Ollama model causes a fail-closed refusal. Activation never unloads a foreign model.
- Helper state, `OLLAMA_MODELS`, startup ownership, loopback policy, selected manifest, model tag, and digest are verified before the transition commits.

## Recovery and compatibility

- A revision-bound transition marker keeps local review paused across an interrupted activation.
- `thalen-helper models recover --yes` re-verifies both trees and restores the original model path without deleting either copy.
- Ordinary move, repair, enable, resume, and stale state writes refuse to cross a pending transition.
- The existing destructive-after-verification `models move` behavior is unchanged and remains separate from non-destructive activation.

## Verification

- New coverage includes exact activation, mismatch refusal, foreign-model protection, pending-transition guards, stale-write refusal, empty-directory comparison, runtime recovery, and idle-only Ollama restart behavior.
- Unsigned builds remain prereleases and can show a Windows SmartScreen warning.
