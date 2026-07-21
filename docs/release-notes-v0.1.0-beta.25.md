# Codex GPU Thalen Helper v0.1.0-beta.25

This unsigned prerelease fixes passive health detection on Windows computers whose display dock exposes more than one adapter with the same name.

## DisplayLink dock compatibility

- Windows may return multiple `Win32_VideoController` records named `DisplayLink USB Device` when a multi-display USB dock is connected.
- Previous releases treated every adapter name as unique and could terminate passive health while building the driver inventory.
- Duplicate names are now coalesced case-insensitively, blank records are ignored, and an available matching driver version is retained.
- If identically named records disagree about their nonblank driver versions, the helper reports the version as unknown instead of choosing one arbitrarily.
- Regression tests cover identical DisplayLink records, case and whitespace variations, missing versions, and conflicting versions without touching live hardware or a real Codex home.

The public-install bootstrap and reviewer behavior are otherwise unchanged from beta.24. The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
