# Task-aware local model routing

Automatic routing is a persistent helper preference shared by every Codex project that uses the same managed `local_gpu_reviewer` installation. It does not write project instruction files and does not require each repository to select a model.

## Fast path for each task

1. Codex calls `local_gpu_plan` with a bounded task description, task kind, expected input size, requested effort, and whether a GPU-heavy workload is active.
2. The passive planner selects only an already-installed catalog model with a current provider-specific validation pass for its exact full digest and whose quantization policy, RAM requirement, available dedicated VRAM, and configured reserve pass policy. Planning never validates, pulls, loads, or runs a model.
3. Codex announces the returned integration, provider, actual model, and purpose.
4. Codex calls `local_gpu_review` with the same routing fields and only the text it explicitly chose to supply.
5. The helper acquires its cross-process single-review lock, repeats model selection and every safety check inside that lock, and only then sends one bounded generation request.
6. Codex reports the model actually returned and independently verifies any useful advice.

Pinned mode remains available for users who always want one validated model. It never silently switches to another model.

## Routing policy

| Effort | Default context request | Selection intent |
|---|---:|---|
| Quick | 8K | Smallest safe installed audited model |
| Standard | 16K | Strongest safe installed audited model up to roughly 16B |
| Deep | 64K | Strongest safe installed audited model |

LM Studio and Qwythos are excluded from routing in beta.12. The current LM Studio inventory API cannot bind a loaded model key to the exact absolute GGUF file that was audited, so the helper returns a fail-closed registration error without inference. Automatic mode selects only eligible Ollama candidates until that trust boundary can be proven.

Every context request is capped by both the model catalog maximum and the helper-wide maximum. A GPU-intensive workload forces quick effort and the smallest safe tier. Current dedicated-VRAM and Windows memory/commit pressure can still refuse the review immediately before generation.

Fresh managed installations default to automatic routing after at least one model passes validation. Existing installations retain their prior pinned behavior during an upgrade until the user explicitly validates suitable installed models and chooses automatic routing:

```text
thalen-helper model routing status
thalen-helper model routing automatic
thalen-helper model routing pinned
```

These commands update helper state only. They do not download or load a model and do not rewrite project files.

## RTX 3090 / 64 GB starting policy

The saved baseline is Q4, 64K for deep work when the chosen model supports it, Flash Attention preferred, Q8_0 K/V cache preferred, at least 1–2 GiB of VRAM reserved, cautious maximum GPU offload, GPU-resident KV cache, model-provided Jinja templates, and optional CPU placement for some MoE experts when the runtime exposes a safe supported control.

The helper directly enforces the parts available through its Ollama boundary: audited installed model selection, digest and Q4 eligibility, context limits, VRAM reserve, resource refusal, single-flight locking, queue-or-skip behavior, and immediate unload by default. Ollama currently manages Flash Attention, K/V cache format, GPU tensor placement, MoE placement, and chat-template internals rather than exposing all of them as stable per-request controls. The plan result reports those settings as provider-managed preferences, not as falsely claimed applied settings.

Attempt 128K only after measuring peak VRAM, prompt-processing speed, and tool reliability. Vision also requires the matching projector. `--no-mmap`, manual tensor routing, TurboQuant, Q3-versus-Q4 speed claims, and reported tokens-per-second remain opt-in experiments.

Use one coherent objective per model context. Checkpoint useful work between unrelated objectives and watch long sessions for loops, repeated tool calls, malformed JSON, repeated prompt processing, context drift, and growing memory use. Every local result remains advisory until verified against code, tests, runtime evidence, or authoritative documentation.

## Provider boundary

Before Ollama generation, the helper accepts either an empty runtime or exactly one running model that matches both its current ownership marker and the requested route. Any untracked CPU-only, GPU-resident, additional, mismatched, or same-name model is foreign, causes `FOREIGN_MODEL_LOADED`, and is never unloaded. A stale or malformed ownership marker also fails closed. Generation uses `keep_alive=0s`; cleanup and the Pause, Disable, and Release controls only observe release and never issue a separate unload by mutable model name.

The LM Studio adapter remains present for future hardening, but registration, pinned routing, and automatic routing are disabled in beta.12. Automatic fallback never assigns an LM Studio directory to `OLLAMA_MODELS`.
