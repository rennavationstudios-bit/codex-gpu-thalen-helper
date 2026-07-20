# Codex GPU Thalen Helper v0.1.0-beta.22

This unsigned prerelease corrects the cold-load timeout uncovered while live-testing beta.21 with Qwythos 9B through LM Studio. It retains beta.19 task-aware routing, beta.20's clean compact UI, and beta.21's truthful loading/reviewing/releasing status.

## Cold local models get their full bounded load budget

- The loopback Ollama and LM Studio clients now disable .NET's separate 100-second `HttpClient` default timeout.
- Existing operation-specific cancellation remains authoritative: inventory requests are bounded to 10 seconds, LM Studio loading and local generation to five minutes, and Ollama pulls to two hours.
- A slow cold load is therefore not misreported as a user cancellation merely because it crosses 100 seconds.
- Caller cancellation, GPU pressure guards, the one-review lock, foreign-model preservation, and exact helper-owned unload verification are unchanged.
- Passive LM Studio inventory continues to run with redirected streams and `CreateNoWindow=true`; the regression suite now asserts that no-console launch contract directly.

## Verification and trust boundary

Deterministic mocked tests set an artificially tiny `HttpClient` timeout and prove that both provider clients still complete within their own explicit operation budget. No real model inference runs in the automated suite.

The installer remains unsigned and this build remains a prerelease. Verify the published SHA-256 checksum and GitHub artifact attestation before running it; Windows SmartScreen may warn about an unknown publisher.
