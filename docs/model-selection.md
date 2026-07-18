# Model selection and validation

Automatic storage placement never chooses removable or network storage. A user may explicitly select an already mounted external drive that Windows exposes as a fixed volume, subject to the normal free-space reserve. The helper records a disconnect warning and expects the same drive letter to be available before Ollama or Codex starts; it does not copy already installed models merely because the storage is external.

The versioned JSON catalog is the allowlist. Arbitrary scraped tags cannot be installed through the managed model-change path. Entries include size, family/parameters, task scope, VRAM/RAM/disk requirements, context bounds, performance tier, CPU suitability, license/source URLs, verification date, and digest where published.

## Selection

Automatic candidates must have a commercial-use-compatible license, `automaticSelectionAllowed=true`, and a current per-model validation pass for the exact installed digest. They are ordered by parameter count and fitted against:

- supported acceleration and a non-integrated GPU;
- usable rather than advertised VRAM;
- system RAM and current available RAM;
- conservative context defaults;
- laptop reduction and runtime/display reserves.

CPU fallback is off unless explicitly opted in. Restricted-license models require explicit acceptance. A requested model cannot exceed the current conservative recommendation.

Entry-tier context is reduced and low-impact mode is on. The MX330 fixture selects at most `qwen2.5-coder:1.5b`; its integrated Intel shared memory contributes zero dedicated VRAM.

## Runtime validation

After a user-authorized pull, setup runs:

1. An ownership check that accepts an empty Ollama runtime or the single exact model already bound to the helper marker, and refuses every untracked loaded model without unloading it.
2. An exact `THALEN_HELPER_OK` response check.
3. A tiny off-by-one review expected to return `OFF_BY_ONE`.
4. `/api/ps` inspection for loaded model, VRAM bytes, context, and expiry.
5. Generation with `keep_alive=0s`, without a separate name-based unload request.
6. Bounded `/api/ps` polling that proves the tracked runtime released before validation evidence is recorded.

After both checks, runtime inspection, unload, and a post-unload check succeed, the helper records only the model tag, full digest, validation protocol, pass time, two durations, and bounded acceleration counters in `model-validations.json`. Prompts, responses, findings, paths, and user data are never recorded. A missing, corrupt, stale-protocol, or digest-mismatched record is ineligible for automatic routing. OOM, timeout, malformed response, wrong output, cancellation, or unload failure removes that model's prior pass and keeps it out of the automatic pool. Guided setup never switches to a different fallback model after the user confirms a named selection. An explicitly configured noninteractive installation may attempt exactly one smaller automatic/commercial candidate. Pre-existing models are not removed or marked as product-owned.

### LM Studio registration

LM Studio registration is fail-closed in beta.12. The current loopback inventory exposes a model key and metadata but not the canonical absolute GGUF file that will be loaded for that key. Therefore `thalen-helper lmstudio register <model-key> <gguf-path> --yes` removes any stale approval and returns `LMSTUDIO_EXACT_FILE_BINDING_UNAVAILABLE` without hashing for approval, loading, or running the model. LM Studio models do not enter automatic or pinned routing until a future runtime contract can bind the loaded key to the exact audited file identity.

## Limitations

Ollama runtime metadata may report full GPU, partial GPU, CPU, or ambiguous processor splits differently across versions. The helper reports evidence conservatively and does not turn an uncertain result into a GPU claim. Model quality and performance vary by driver, quantization, prompt, thermal limits, and concurrent workloads.
