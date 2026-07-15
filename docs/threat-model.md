# Threat model

## Assets

- Codex configuration/instructions and authentication boundaries.
- User-supplied repository text.
- Local CPU/RAM/GPU availability.
- Ollama models and model storage.
- Integrity of installer/update artifacts.

## Adversaries and failure sources

- Malicious text supplied to the local model.
- Incorrect or hallucinated model output.
- A non-loopback or impersonated Ollama endpoint.
- Malformed/oversized Ollama responses.
- Tampered release downloads or dependency compromise.
- Unsafe configuration merge/uninstall behavior.
- Concurrent review/startup processes consuming resources or creating duplicates.
- Accidental disclosure through logs/diagnostics/repository contents.

## Controls

| Threat | Primary controls | Residual risk |
|---|---|---|
| Prompt injection | Explicit untrusted-data framing; no execution/filesystem/shell tools | Model advice may still be misleading |
| Secret disclosure | Only caller-supplied text; no prompt logs; docs prohibit secrets | Codex/user can still explicitly supply sensitive text |
| Endpoint impersonation | Loopback URI validation, listener checks, exact TCP-owner PID mapping, current-user SID match, exact Ollama executable name, and valid expected-publisher Authenticode verification before HTTP bytes | A compromised administrator/current-user security boundary or a malicious but still validly signed Ollama build remains trusted |
| Model substitution | Catalog digest, selected-tag, manifest, and configured-directory checks before activation/review | A privileged local attacker can replace both runtime and trusted state |
| Resource exhaustion | Input/response/context/output bounds, timeouts, single-flight semaphore, cancellation/unload | Ollama/driver may still hang or page under unusual faults |
| Duplicate Ollama | Named startup semaphore, endpoint/process checks, exact trusted executable-path termination policy | Third-party startup software can race outside helper control |
| Config corruption | TOML parse, backup, markers, atomic write, startup validator rollback | External concurrent editors may require manual reconciliation |
| Supply-chain tampering | Pinned versions/actions, locked restore, checksum/signature/publisher checks, CodeQL/dependency review, SBOM/attestation, protected tags/environment | Unsigned beta remains subject to SmartScreen and local trust decisions |
| Local executable search order | `nvidia-smi.exe` resolves only from trusted absolute Windows/NVIDIA installation paths; shell execution and current-directory/`PATH` search are disabled | Nonstandard NVIDIA layouts lose enhanced telemetry and fall back to conservative DXGI data |
| Untrusted release tag | Protected `v*` tag ruleset/environment plus exact event/tag/commit and `main` ancestry checks | Repository administrators and compromised release authority remain trusted |
| Uninstall data loss | Ownership state, marked removal, explicit model-delete consent, verified model move rollback | User-modified managed sections may require backup recovery |

## Out of scope

Compromise of the local Windows account/administrator, a malicious Ollama binary already trusted by the user, model-training supply-chain guarantees, Codex/OpenAI service security, and physical access are outside this helper's enforceable boundary.
