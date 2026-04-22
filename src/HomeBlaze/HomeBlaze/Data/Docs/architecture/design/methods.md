---
title: Methods and Operations
navTitle: Methods
status: Partial
---

# Methods and Operations

## Overview

Subjects expose executable behavior through methods marked with `[Operation]` (state-changing) or `[Query]` (read-only). These are first-class citizens of the knowledge graph — discoverable via registry, invocable from the UI, MCP tools, and protocol methods (e.g., OPC UA).

## Current State [Implemented]

Operations and queries are first-class registry members in HomeBlaze today:

- `[Operation]` and `[Query]` attributes in `HomeBlaze.Abstractions` derive from `Namotion.Interceptor.Attributes.SubjectMethodAttribute`, so the source generator discovers them like any other subject method
- The source generator emits a `SubjectMethodMetadata` entry per method into `IInterceptorSubject.Methods`, with typed parameters, reflection attributes, and an invocation delegate (no runtime reflection)
- `RegisteredSubjectMethod` (in `Namotion.Interceptor.Registry`) exposes each method alongside properties and attributes; `RegisteredSubject.TryGetMethod(name)` looks one up
- `MethodInitializer` (in `HomeBlaze.Services`) implements `ISubjectMethodInitializer` and attaches HomeBlaze-specific `MethodMetadata` (title, confirmation, parameter metadata) to each registered method on attach
- `[MethodAttribute]` enables property-backed method attributes (e.g., `Start_IsEnabled` for conditional enable/disable)
- Blazor UI renders operations as buttons with parameter dialogs, confirmation, and result display
- MCP tools `list_methods` and `invoke_method` in `Namotion.Interceptor.Mcp` (in progress, [PR #158](https://github.com/RicoSuter/Namotion.Interceptor/pull/158))

## Core migration [Done]

The previously planned "move operation abstractions out of HomeBlaze" goal has landed without a separate package:

| Previous plan (target: `Namotion.Interceptor.Reflection`) | Actual landing site |
|---|---|
| `[Operation]` / `[Query]` base attribute | `Namotion.Interceptor.Attributes.SubjectMethodAttribute` (core) |
| `MethodMetadata`, `MethodParameter` types | `Namotion.Interceptor.SubjectMethodMetadata` / `SubjectMethodParameterMetadata` (core) |
| Lifecycle handler registering methods | `Namotion.Interceptor.Registry.Abstractions.ISubjectMethodInitializer` + `RegisteredSubjectMethod` (registry) |
| Method discovery / invocation | Source-generated into `IInterceptorSubject.Methods`; invoked via `RegisteredSubjectMethod.Invoke` |

No runtime reflection is involved, so the "Reflection" package name was obsolete anyway. The HomeBlaze-specific `MethodMetadata` (title, confirmation, parameter UI metadata) stays in HomeBlaze and is attached per method by `MethodInitializer` via the `ISubjectMethodInitializer` hook.

What stays in HomeBlaze:
- Blazor UI components (parameter dialogs, confirmation, result display)
- HomeBlaze-specific `MethodMetadata` (UI title, icon, `RequiresConfirmation`, parameter display metadata)
- `MethodInitializer` that translates `[Operation]` / `[Query]` attribute data into `MethodMetadata` on each registered method

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
| Core abstractions location | `Namotion.Interceptor` (core) + `Namotion.Interceptor.Registry` (no separate `Namotion.Interceptor.Reflection` package) | Methods are source-generated with zero reflection, so a "Reflection" package would be misnamed; core + registry is the right split |
| RPC semantics | Execute-all, not last-writer-wins | Operations are commands, not state — every invocation matters |

## Open Questions

- Wire format for operation proxying (request/response message structure)
- Timeout and cancellation semantics for cross-instance RPC
- How to handle long-running operations (async progress reporting?)
- Should operations be queueable (invoke later, retry on failure)?
- Authorization for cross-instance operation invocation (see [Security](security.md))
