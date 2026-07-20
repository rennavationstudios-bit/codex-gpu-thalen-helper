# Codex GPU Thalen Helper v0.1.0-beta.20

This unsigned prerelease is a focused Control Center hotfix. It preserves beta.19 task-aware routing, structured advisory output, provider safety, protected-file merging, and automatic unload behavior.

## Visible fixes

- The compact three-dot Advanced menu no longer uses a pill-shaped owner-drawn button. It is now a transparent keyboard-accessible ellipsis glyph, removing the rectangular background and square focus-mask artifacts entirely.
- The GPU status strip now follows the helper-owned active-model tracker while the Control Center is open. Reviews started by Codex show the tracked model and provider, such as **Qwythos 9B review via LM Studio** or **Qwen3 8B review via Ollama**.
- After the tracker clears, the strip restores the exact prior passive status, including manual-startup or safety warnings. A tracker proves helper ownership and release responsibility; it does not by itself claim provider-confirmed GPU residency.
- The observer runs every 500 milliseconds on the UI thread and reads one small helper-owned local tracker file. It does not query provider inventories, start a process, load a model, run inference, or widen any trust boundary.

## Safety and compatibility

The activity display never treats an arbitrary provider model as helper-owned. It reports only the strict tracker already used by the review lifecycle and cleanup controls. Existing loopback enforcement, single-review locking, resource-pressure refusal, foreign-model preservation, verified unload, exact-file LM Studio registration, and no-automatic-download behavior remain unchanged.

The complete isolated Windows test suite passes **387/387** tests, including nested-gradient rendering coverage for the transparent menu glyph and active-to-idle status restoration.

The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
