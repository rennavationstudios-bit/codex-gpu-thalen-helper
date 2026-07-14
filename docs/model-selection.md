# Model selection and validation

The versioned JSON catalog is the allowlist. Arbitrary scraped tags cannot be installed through the managed model-change path. Entries include size, family/parameters, task scope, VRAM/RAM/disk requirements, context bounds, performance tier, CPU suitability, license/source URLs, verification date, and digest where published.

## Selection

Automatic candidates must have a commercial-use-compatible license and `automaticSelectionAllowed=true`. They are ordered by parameter count and fitted against:

- supported acceleration and a non-integrated GPU;
- usable rather than advertised VRAM;
- system RAM and current available RAM;
- conservative context defaults;
- laptop reduction and runtime/display reserves.

CPU fallback is off unless explicitly opted in. Restricted-license models require explicit acceptance. A requested model cannot exceed the current conservative recommendation.

Entry-tier context is reduced and low-impact mode is on. The MX330 fixture selects at most `qwen2.5-coder:1.5b`; its integrated Intel shared memory contributes zero dedicated VRAM.

## Runtime validation

After a user-authorized pull, setup runs:

1. An exact `THALEN_HELPER_OK` response check.
2. A tiny off-by-one review expected to return `OFF_BY_ONE`.
3. `/api/ps` inspection for loaded model, VRAM bytes, context, and expiry.
4. Explicit unload through generation with `keep_alive=0`.
5. A second `/api/ps` check proving unload.

Durations and safe counters are recorded; prompts/responses are not. OOM, timeout, malformed response, wrong output, or unload failure keeps the integration disabled. Guided setup never switches to a different fallback model after the user confirms a named selection. An explicitly configured noninteractive installation may attempt exactly one smaller automatic/commercial candidate. Pre-existing models are not removed or marked as product-owned.

## Limitations

Ollama runtime metadata may report full GPU, partial GPU, CPU, or ambiguous processor splits differently across versions. The helper reports evidence conservatively and does not turn an uncertain result into a GPU claim. Model quality and performance vary by driver, quantization, prompt, thermal limits, and concurrent workloads.
