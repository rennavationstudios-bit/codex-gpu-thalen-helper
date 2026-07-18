# Task-aware local model routing

Automatic routing is a persistent helper preference shared by every Codex project that uses the same managed `local_gpu_reviewer` installation. It does not write project instruction files and does not require each repository to select a model.

## Fast path for each task

1. Codex calls `local_gpu_plan` with a bounded task description, task kind, expected input size, requested effort, and whether a GPU-heavy workload is active.
2. The passive planner considers only already-installed catalog models with a current provider-specific validation pass for their exact identity. It applies quantization policy, RAM requirements, available dedicated VRAM, storage/path checks, and the configured reserve. Planning never validates, downloads, loads, or runs a model.
3. Codex announces the returned integration, provider, actual model, and bounded purpose.
4. Codex calls `local_gpu_review` with the same routing fields and only the text it explicitly chose to supply.
5. The helper acquires its cross-process single-review lock, repeats routing and every safety check inside that lock, and only then sends one bounded generation request.
6. Codex reports the provider and model that actually ran and independently verifies any useful advice.

Pinned mode remains available for users who always want one validated provider/model route. It never silently switches to another model.

## Routing policy

| Effort | Default context request | Selection intent |
|---|---:|---|
| Quick | 8K | Smallest safe installed audited model |
| Standard | 16K | Strongest safe installed audited model up to roughly 16B |
| Deep | 64K | Strongest safe installed audited model |

Eligible Ollama models and the explicitly registered catalog-audited LM Studio/Qwythos route can participate in automatic selection. Qwythos is not a generic doorway to other GGUF files: it must be the exact existing catalog path, size, file identity, and full digest, with a current signed-CLI and loopback validation pass. Older beta.11 LM Studio records require explicit revalidation before they are eligible.

If LM Studio is closed, untrusted, busy with a user-owned model, or cannot prove the exact registered file, automatic mode may choose another independently eligible Ollama route. It never assigns an LM Studio directory to `OLLAMA_MODELS`, silently downloads a fallback, or unloads the user's LM Studio instance.

Every context request is capped by both the model catalog maximum and the helper-wide maximum. A GPU-intensive workload forces quick effort and the smallest safe tier. Current dedicated-VRAM and Windows memory/commit pressure can still refuse the review immediately before generation.

Fresh managed installations default to automatic routing after at least one model passes validation. Existing installations retain their prior pinned behavior during an upgrade until the user explicitly validates suitable installed models and chooses automatic routing:

```text
thalen-helper model routing status
thalen-helper model routing automatic
thalen-helper model routing pinned
```

These commands update helper state only. They do not download or load a model and do not rewrite project files.

## Runtime policy

The baseline preference is Q4 for ordinary catalog models, 64K for deep work only when the chosen model and current hardware support it, Flash Attention preferred, Q8_0 K/V cache preferred, at least 1-2 GiB of VRAM reserved, cautious maximum GPU offload, GPU-resident KV cache, model-provided Jinja templates, and optional CPU placement for some MoE experts when a provider exposes a safe supported control. A catalog-audited non-Q4 route such as the current BF16 Qwythos file is eligible only through its explicit catalog exception and higher hardware requirements; it does not weaken the default policy for other models.

The helper directly enforces the parts exposed by each provider boundary: audited installed model selection, exact identity and eligibility, context limits, VRAM reserve, resource refusal, single-flight locking, queue-or-skip behavior, and immediate unload by default. Provider-managed settings are reported as preferences, not falsely claimed as applied settings.

Attempt 128K only after measuring peak VRAM, prompt-processing speed, and tool reliability. Vision also requires the matching projector. `--no-mmap`, manual tensor routing, TurboQuant, Q3-versus-Q4 speed claims, and reported tokens-per-second remain opt-in experiments.

Use one coherent objective per model context. Checkpoint useful work between unrelated objectives and watch long sessions for loops, repeated tool calls, malformed JSON, repeated prompt processing, context drift, and growing memory use. Every local result remains advisory until verified against code, tests, runtime evidence, or authoritative documentation.

## Provider boundary

Before Ollama generation, the helper accepts either an empty runtime or exactly one running model that matches both its current ownership marker and the requested route. Any untracked CPU-only, GPU-resident, additional, mismatched, or same-name model is foreign, causes `FOREIGN_MODEL_LOADED`, and is never unloaded. A stale or malformed ownership marker also fails closed. Generation uses `keep_alive=0s`; cleanup and the Pause, Disable, and Release controls only observe release and never issue a separate unload by mutable model name.

For LM Studio, the helper requires its signed current-user CLI inventory to bind the exact catalog key to the catalog-relative GGUF path and size. It holds the exact file identity, full digest, and pinned models-root/path lease while it verifies the loopback REST process, loads the key, binds the returned instance back to that same file, generates through that instance, unloads only that instance, and confirms its absence through both REST and `lms ps`. Any foreign loaded instance or changed identity fails closed and is left untouched.

The LM Studio REST load request still names the audited catalog key. Another local client deliberately loading that same key during the small pre-load interval could race with it. The lease, path and file pinning, exact-instance post-load check, and fail-closed cleanup materially narrow the risk, but users should not manually load the same key during helper work. A future hardening step is a unique CLI-assigned identifier once that upstream contract is suitable.

Both providers are loopback-only: Ollama on `127.0.0.1:11434` and LM Studio on `127.0.0.1:1234`. A provider that is network-exposed is ineligible.
