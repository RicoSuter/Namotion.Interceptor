---
title: Methods and Operations
navTitle: Methods
status: Partial
---

# Methods and Operations

## Overview [Implemented] / [Planned]

Subjects expose executable behavior through methods marked with `[Operation]` (state-changing) or `[Query]` (read-only). These are first-class citizens of the knowledge graph — discoverable via registry, invocable from the UI, MCP tools, and protocol methods (e.g., OPC UA).

## Current State [Implemented]

Operations and queries are implemented in HomeBlaze today:

- `[Operation]` and `[Query]` attributes defined in `HomeBlaze.Abstractions`
- Method discovery via `MethodMetadata` registered as dynamic properties in the registry
- `MethodPropertyInitializer` registers `MethodMetadata` (with `InvokeAsync` capability) as dynamic properties, enabling `[PropertyAttribute]` on operations (e.g., `IsEnabled` for conditional enable/disable)
- Blazor UI renders operations as buttons with parameter dialogs, confirmation, and result display
- MCP tools `list_methods` and `invoke_method` in `Namotion.Interceptor.Mcp` (in progress, [PR #158](https://github.com/RicoSuter/Namotion.Interceptor/pull/158))

## Migration to Namotion.Interceptor.Reflection [Planned]

Operations currently use registry attributes (`MethodMetadata` dynamic properties) but all types still live in HomeBlaze. The goal is to move the core abstractions into a `Namotion.Interceptor.Reflection` package so operations are reusable across all interceptor applications (OPC UA method mapping, MCP tool support, etc.).

What should move to `Namotion.Interceptor.Reflection`:
- `[Operation]` and `[Query]` attributes
- `MethodMetadata`, `MethodParameter`, `MethodKind` types
- `MethodPropertyInitializer` lifecycle handler
- Method discovery and invocation via `MethodMetadata.InvokeAsync`

What stays in HomeBlaze:
- Blazor UI components (parameter dialogs, confirmation, result display)
- Domain-specific operation patterns

## Cross-Instance Operation Proxying (RPC) [Planned]

Operations invoked on a remote instance (e.g., operator on central UNS invokes an operation on a satellite's subject) need to be forwarded to the owning instance and executed there. This uses WebSocket message types 5-6 (planned).

### Design Goals

- **Every invocation executes.** Operations are not deduplicated or last-writer-wins like property sync. Two operators invoking the same operation from different UNS instances must both execute — they are separate commands, not conflicting state.
- **Results flow back to the caller.** The invoking instance receives the return value or error from the owning instance.
- **Operations map to protocol methods.** OPC UA methods on server subjects should proxy to the underlying operation.

### Idempotency

Operations proxied via WebSocket have no defined idempotency semantics yet. If the WebSocket drops mid-RPC:

- Does the caller retry automatically?
- Can the callee detect duplicate invocations?
- What are the at-least-once vs. at-most-once guarantees?

For most operations, at-most-once (fail on disconnect) is acceptable. For critical operations (e.g., physical actuator commands), stronger guarantees may be needed. An optional idempotency key per invocation could enable callers to safely retry.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Method attributes | `[Operation]` (state-changing) and `[Query]` (read-only) | Clear semantic distinction enables different authorization defaults and UI treatment |
| Migration to Namotion.Interceptor.Reflection | Planned — attributes and discovery to move into dedicated package | Enables OPC UA method mapping, MCP tool support, and reuse outside HomeBlaze |
| RPC semantics | Execute-all, not last-writer-wins | Operations are commands, not state — every invocation matters |

## Open Questions

- Wire format for operation proxying (request/response message structure)
- Timeout and cancellation semantics for cross-instance RPC
- How to handle long-running operations (async progress reporting?)
- Should operations be queueable (invoke later, retry on failure)?
- Authorization for cross-instance operation invocation (see [Security](security.md))
