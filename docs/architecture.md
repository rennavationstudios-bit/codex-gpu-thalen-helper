# Architecture

## Components

```mermaid
flowchart LR
  C["Codex primary agent"] -->|"stdio MCP; explicit supplied text"| M["local_gpu_reviewer"]
  M -->|"verified HTTP loopback"| O["Ollama"]
  M -->|"verified HTTP loopback + signed CLI binding"| LM["Registered LM Studio route"]
  O --> L["Selected local model"]
  LM --> L
  U["Control Center / CLI"] --> S["Product state"]
  U --> Cfg["Managed Codex config and instructions"]
  U --> O
  M --> S
```

`ThalenHelper.Core` owns hardware/model/storage decisions, provider communication, deterministic task-aware routing, bounded structured-result parsing, configuration surgery, state, lifecycle controls, install/repair/update/uninstall, and diagnostics. The CLI and WinForms Control Center are thin interfaces over that core. `ThalenHelper.Mcp` hosts only annotated health, plan, and review methods through the official MCP C# SDK.

## Trust boundaries

- Codex decides whether to supply text to the local reviewer.
- Supplied text is untrusted data, not executable instruction.
- Local-model output is untrusted advice.
- Structured findings are shape-validated model claims, not confirmed observations; their locations and evidence refer only to supplied text until Codex independently checks them.
- The reviewer cannot independently read the repository or use a shell.
- Ollama requests are limited to documented `/api/tags`, `/api/ps`, `/api/generate`, `/api/pull`, and `/api/delete` paths; the reviewer itself uses inventory/runtime/generate only.
- The base URI must be unauthenticated loopback HTTP. Redirects and proxies are disabled. Before sending HTTP bytes, the production transport maps the exact TCP connection to its owning Windows process and requires a current-user, validly signed `Ollama Inc.` `ollama.exe` or `ollama app.exe` peer.
- The MCP server opens no network listener; its only server transport is stdio.

## Resource coordination

A named cross-process semaphore limits helper inference to one request. A named cancellation event lets pause/release operations cancel an active request. Low-impact mode uses short context/output bounds and `keep_alive=0`. Health checks call only inventory/runtime endpoints. Planning uses deterministic local routing and provider inventory without generation. Review parsing caps structured findings and field lengths, preserves the original bounded response for compatibility, and writes neither form to product state or diagnostics.

The Control Center observes two deliberately separate local records. `active-routed-model.txt` is the strict, fail-closed ownership reference used by cleanup controls. `active-review.json` is a short-lived display-only lifecycle signal for loading, reviewing, releasing, or attention states. The latter never authorizes unload, release, routing, health, configuration, or any provider mutation; malformed, future, and expired records are ignored. This separation lets a long LM Studio load become visible before the provider returns the exact instance identity needed for ownership proof without weakening the foreign-model boundary.

Ollama auto-start uses a separate named cross-process semaphore. It checks a responding endpoint first, then checks existing Ollama processes. It does not spawn a duplicate when an existing process is unhealthy.

## Configuration ownership

The product state JSON records only operational ownership and settings. The MCP table, automatic local GPU guidance, and opt-in reliability baseline use distinct start/end markers. New content is validated before and after atomic write; failures restore the exact original. Uninstall removes only marked content and product-owned state/startup entries, including the exact display-only activity file after owned reviewer processes are stopped.

An existing unmarked `local_gpu_reviewer` table is a separate ownership boundary. Detection enters preservation mode before any Ollama, model-directory, startup, model, or control mutation. Product controls fail closed, and uninstall leaves the existing integration/runtime untouched.

## Compatibility workaround

The server sends ordinary bounded text-generation requests to the selected verified loopback provider. The Ollama request uses `model`, `prompt`, `stream=false`, `think=false`, `keep_alive`, and bounded options. The server never constructs a Responses API `agent_message` input item and is not registered as a native Codex custom subagent. Task-specific JSON output is only an advisory response contract layered on that ordinary local generation path.
