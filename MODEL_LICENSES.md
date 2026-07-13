# Model license notices

No model weights are distributed in this repository or installer. The helper downloads a model from Ollama only after user authorization. Each model retains its upstream license.

Catalog entries verified for the initial beta:

| Ollama tag | Catalog policy | License |
|---|---|---|
| `qwen2.5-coder:0.5b` | automatic selection allowed | Apache License 2.0 |
| `qwen2.5-coder:1.5b` | automatic selection allowed | Apache License 2.0 |
| `qwen2.5-coder:3b` | explicit selection only | Qwen Research License / non-commercial terms |
| `qwen2.5-coder:7b` | automatic selection allowed | Apache License 2.0 |
| `qwen2.5-coder:14b` | automatic selection allowed | Apache License 2.0 |
| `qwen2.5-coder:32b` | explicit selection only in the initial catalog | Apache License 2.0 |
| `qwen3-coder:30b` | automatic selection allowed on verified high-memory hardware | Apache License 2.0 |

Exact license/source URLs, immutable digests where available, and verification dates are in `model-catalog/models.v1.json`. Users must review upstream terms. A restricted model is never auto-selected and requires explicit license acceptance.
