---
title: Methods and Operations
navTitle: Methods
status: Partial
---

# Methods and Operations

## Overview [Implemented] / [Planned]

Subjects expose executable behavior through methods marked with `[Operation]` (state-changing) or `[Query]` (read-only). These are first-class citizens of the knowledge graph: discoverable via registry, invocable from the UI, MCP tools, and protocol methods (for example OPC UA). Operations are the platform's control plane. Unlike property sync (the reflection plane, last-writer-wins), a command is an intent to act: it must not be silently dropped or resolved by last-writer-wins. See the reflection-plane vs control-plane framing in the [Architecture Overview](../overview.md).

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

- **Distinct invocations all execute.** Operations are commands, not state. Two operators invoking the same operation from different UNS instances are two separate commands and both execute. They are never deduplicated or resolved by last-writer-wins the way property values are.
- **A single command executes once, even across a retry.** "Distinct invocations all execute" is about different commands. It does not mean a dropped-and-retried command should run twice. Delivery reliability is handled separately (below).
- **Results flow back to the caller.** The invoking instance receives the return value or error from the owning instance.
- **Operations map to protocol methods.** OPC UA methods on server subjects proxy to the underlying operation.

### Delivery Semantics [Decided]

The owning node is authoritative for a command: it is the single place the command is authorized and executed. The contract:

- **At-least-once transport with an idempotency key.** Each invocation carries a caller-generated idempotency key. The callee keeps a bounded dedup window keyed by it, so a transport retry after a dropped connection executes the command exactly once. This is the default for operations that actuate.
- **At-most-once fallback.** An invocation without an idempotency key is at-most-once: on a mid-RPC disconnect it may not have executed, and the caller cannot assume it did. Acceptable only for safe-to-lose operations.
- **Authorize and execute on the owner.** Authorization is re-validated on the owning node using the propagated caller identity, never trusted from the calling node alone (see [Security](security.md)).
- **Confirmation is via read-back, not the RPC result.** A command's effect appears on the reflection plane as normal property changes. The RPC result and the resulting state changes travel on different paths and are not ordered relative to each other, so a caller must treat the confirmed state, not the return value, as proof the action took effect.

In-flight commands are not durable. A command whose owner fails over mid-execution has no persisted record on the promoted node. With an idempotency key the caller can safely retry against the new owner; without one the outcome is unknown. This is why actuation should use keys.

### Control Authority [Planned]

Being allowed to execute a command is not the same as being the one node currently allowed to drive a device. Exclusive control (one writer at a time for a given subject or device) is a separate concern from delivery:

- Who currently holds write authority over a subject (an operator, a satellite, an agent) needs to be explicit, so a second writer cannot silently contend.
- The mechanism (a write-ownership token or lock on the subject, or reliance on device-side exclusivity where the protocol supports it) is not yet designed. It connects to the HA single-writer problem in [Resilience](resilience.md).

Cross-instance operation proxying also carries record reads: a database-backed subject's `[Query]` methods are proxied to the owning node rather than replicating record state. See [Records and Persistence](records-and-persistence.md).

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Method attributes | `[Operation]` (state-changing) and `[Query]` (read-only) | Clear semantic distinction enables different authorization defaults and UI treatment |
| Migration to Namotion.Interceptor.Reflection | Planned — attributes and discovery to move into dedicated package | Enables OPC UA method mapping, MCP tool support, and reuse outside HomeBlaze |
| RPC semantics | Distinct invocations all execute, never last-writer-wins | Operations are commands, not state. Different commands must all run |
| RPC delivery | At-least-once with idempotency key and callee-side dedup; at-most-once without a key | A retried command runs once. Actuation uses keys; safe-to-lose operations may skip them |
| RPC authorization | Re-validated and executed on the owning node with propagated caller identity | The calling node is not trusted to have authorized the command (see Security) |
| Command confirmation | Via read-back of resulting state, not the RPC return value | Result and state travel on different paths and are not mutually ordered |

## Open Questions

- Wire format for operation proxying (request/response message structure, idempotency key field)
- Idempotency dedup window size and eviction policy on the callee
- Timeout and cancellation semantics for cross-instance RPC
- How to handle long-running operations (async progress reporting?)
- Control-authority mechanism: write-ownership token/lock vs device-side exclusivity (see [Resilience](resilience.md))
