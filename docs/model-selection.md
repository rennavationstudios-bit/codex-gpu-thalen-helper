# Model selection and validation

The base installer is passive: it does not download, register, load, validate, or run a model. Model setup starts only after the user chooses a provider path and confirms the exact action.

For Ollama, the wizard asks where models should be stored. It shows the selected drive type, current free space, approximate download size, temporary overhead, and required safety reserve before it can continue. Automatic storage placement never chooses removable or network storage. A user may explicitly select an already mounted external drive that Windows exposes as a fixed volume, subject to the same reserve. The helper records a disconnect warning and expects the same drive letter to be available before Ollama or Codex starts; it does not copy installed models merely because storage is external.

The versioned JSON catalog is the allowlist for both providers. Arbitrary scraped Ollama tags and arbitrary GGUF files cannot enter the managed route merely because a provider can load them. Entries include provider, size, family/parameters, task scope, VRAM/RAM/disk requirements, context bounds, performance tier, CPU suitability, license/source URLs, verification date, and digest where published.

## Hardware-aware choices

The setup list is generated for the current computer, not for a preferred example GPU. Models are fitted against:

- supported acceleration and a non-integrated GPU;
- usable rather than advertised dedicated VRAM;
- current available dedicated VRAM when measurable;
- system RAM and current available RAM;
- conservative context and runtime reserves;
- laptop limits; and
- the chosen Ollama destination's free-space reserve.

Models outside the conservative budget remain unavailable with an explanation. CPU fallback is off unless explicitly opted in. Restricted-license models require explicit acceptance. A requested model cannot exceed the current conservative recommendation. Setup validates or acquires one specifically named model at a time; users may repeat the flow later to add other eligible models to the automatic pool.

Entry-tier context is reduced and low-impact mode is on. Hardware fixtures such as the MX330 test case are regression boundaries only; production selection is dynamic for the hardware actually detected.

Automatic candidates must have a commercial-use-compatible license, `automaticSelectionAllowed=true`, and a current provider-specific validation pass for the exact installed identity. A missing, corrupt, legacy-protocol, stale, or digest-mismatched pass keeps that model out of automatic routing.

## Ollama acquisition and runtime validation

A missing Ollama model can be acquired only after the user confirms the exact tag, approximate size, and destination. A matching manifest is only a local hint: Ollama may repair or download that same selected model if inventory disagrees. Guided setup never switches to or downloads a different fallback model.

After a user-authorized pull, or when validating an existing audited model, setup runs:

1. An ownership check that accepts an empty Ollama runtime or the single exact model already bound to the helper marker, and refuses every untracked loaded model without unloading it.
2. An exact `THALEN_HELPER_OK` response check.
3. A tiny off-by-one review expected to return `OFF_BY_ONE`.
4. `/api/ps` inspection for loaded model, VRAM bytes, context, and expiry.
5. Generation with `keep_alive=0s`, without a separate name-based unload request.
6. Bounded `/api/ps` polling that proves the tracked runtime released before validation evidence is recorded.

After both checks, runtime inspection, and post-release verification succeed, the helper records only the model tag, full digest, validation protocol, pass time, two durations, and bounded acceleration counters in `model-validations.json`. Prompts, responses, findings, paths, and user data are never recorded. OOM, timeout, malformed response, wrong output, cancellation, or release failure removes that model's prior pass and keeps it out of the automatic pool. Pre-existing models are not removed or marked as product-owned.

## LM Studio registration

LM Studio is an existing-model registration path, not a model downloader. The helper does not install LM Studio or download, copy, move, import, rename, or substitute a GGUF. The current bundled catalog supports the existing Qwythos model only; this is not a claim of arbitrary GGUF compatibility.

The user selects the exact Qwythos GGUF already indexed beneath the current user's LM Studio models root and explicitly confirms bounded validation. Registration then fails closed unless all of the following hold:

1. The canonical current-user `lms` executable is present and Authenticode-verified before its read-only `ls --json` and `ps --json` inventory is trusted.
2. The catalog model key maps uniquely to the exact catalog-relative path and expected size.
3. The selected path is the same regular file, its identity remains stable, and its full SHA-256 equals the catalog digest while the helper holds it read-only.
4. The LM Studio models root and any local junction are pinned by identity for the bounded operation so the indexed path cannot be silently redirected.
5. The LM Studio REST endpoint is loopback-only and belongs to the verified current-user process.
6. The helper loads the audited catalog key, then proves that the exact returned instance maps back to the same audited GGUF before generation.
7. Validation succeeds through that exact instance.
8. Cleanup unloads only the helper-created instance and requires both REST inventory and signed `lms ps` inventory to show it absent.

Only after all checks succeed does the exact Qwythos registration enter pinned or automatic routing. A beta.11 record does not contain the current CLI/path-lease evidence and is intentionally ineligible until the user explicitly revalidates the same existing file. A changed file, moved path, replaced junction, untrusted CLI, foreign loaded instance, or unload mismatch fails closed without substituting another provider model or unloading user-owned work.

### Residual same-key load race

LM Studio's REST load request still names the audited catalog key. A separate local client deliberately trying to load that same key during the short interval between the helper's inventory check and REST load could race with the helper. The helper mitigates this by holding its cross-process review lease, pinning the model namespace and file identity, binding the exact returned instance before generation, and failing closed on any mismatch. Do not manually load the same Qwythos key while helper registration or review is running. A future hardening step is to use a unique CLI-assigned identifier when that upstream contract is suitable for this trust boundary.

## Limitations

Both providers must remain loopback-only: Ollama on port 11434 and LM Studio on port 1234. Ollama runtime metadata may report full GPU, partial GPU, CPU, or ambiguous processor splits differently across versions. The helper reports evidence conservatively and does not turn an uncertain result into a GPU claim. Model quality and performance vary by driver, quantization, prompt, thermal limits, storage, and concurrent workloads.
