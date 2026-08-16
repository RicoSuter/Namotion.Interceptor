# Connector Diagnostics Merge-Readiness Cleanup

## Context

PR #454 introduces one grouped diagnostics model for every connector, adds queue and drop diagnostics, and aligns connector liveness and error reporting. The branch also contains extensive connector-specific tests and documentation. The latest `master`, including #468 and #469, was merged before this design was written. The merged baseline passes the repository's non-integration test suite.

The review found that the architecture is generally sound. The main design defect is narrower: `QueueMetrics` exposes a registration state machine that is easy to misuse and is not fully correct under concurrency. There is also public API and constructor plumbing that should be simplified before merge. None of the affected APIs requires backward compatibility, so the final surface should be judged only by correctness, clarity, performance, and connector-authoring usability.

## Architecture Decision

Keep the current diagnostics architecture and feature scope.

The following choices remain:

- Connectors own mutable `ConnectorMetrics` or `SourceMetrics` objects.
- Consumers receive stable, read-only `ConnectorDiagnostics` or `SourceDiagnostics` views.
- Protocol-specific diagnostics derive from the shared views and expose only protocol-specific facts.
- Diagnostic views are created once and read through to live state. Reading diagnostics does not create a snapshot object.
- Liveness and its transition time use one atomic immutable state value.
- `SubjectConnectorBase` owns the hosted-service diagnostics lifecycle.
- `ConnectorRunAttempt` remains the shared connector-authoring primitive for per-attempt cancellation and injected-kill state.
- Queue depth is sampled from the owning queue. The hot enqueue and dequeue paths do not maintain an additional diagnostics counter.

This design gives external connector authors a writable metrics object without exposing it through `ISubjectConnector`. It also keeps observation allocation-free in the common case and gives all connectors the same semantics.

### Rejected Redesigns

- Returning immutable diagnostic snapshots would make cross-property coherence simple, but it would allocate on every observation and make polling expensive.
- Exposing diagnostics only through interfaces would weaken the common behavior and force consumers to cast through a hierarchy of optional capabilities.
- Publishing diagnostic events would require subscription lifetime management and a second cache for current state.
- Putting public mutators on diagnostics would let consumers alter another connector's reported health.
- Maintaining every gauge eagerly would simplify reads but add synchronization to high-frequency producer and consumer paths.

None of these alternatives improves correctness, performance, and usability together enough to justify rewriting the current model.

## Queue Registration Redesign

Replace `QueueMetrics.Register`, `BeginRegister`, and `Deregister` with one atomic registration operation:

```csharp
public IDisposable Register(Func<int> depth, Func<long>? dropped, int? capacity);
```

Each successful call creates a unique registration object containing the providers and capacity. `QueueMetrics` stores that identity in its atomic snapshot. The returned handle releases only that identity.

Registration follows these rules:

1. The compare-exchange loop checks for an active registration inside the atomic update. Two concurrent registrations cannot both succeed.
2. Disposing a handle folds that provider's drop count into the accumulator and clears it only when the same registration is still active.
3. A stale or repeatedly disposed handle is a no-op. It cannot release a later registration.
4. Capacity remains visible between short-lived queue instances, matching the current behavior.
5. `AddDropped`, epoch reset, registration release, and diagnostics reads preserve one active registration identity while replacing surrounding snapshots.
6. Provider exceptions continue to produce zero rather than escape from diagnostics getters.

Callers with a scoped queue retain the returned handle in a `using`. Callers whose provider lives as long as the metrics object may intentionally leave the handle undisposed.

This reduces the public state machine, fixes the concurrent registration race, and fixes stale-handle deregistration without adding work to queue operations.

## Public API Hardening

Perform a line-by-line review of the new public API with these decisions:

- Keep the grouped diagnostics types, metrics types, `SubjectConnectorBase`, `ConnectorRunAttempt`, `StateChangeTime`, and queue diagnostic blocks.
- Keep the removal of the old flat connector-specific diagnostic members. Do not add obsolete forwarding aliases, because backward compatibility is not required and aliases would leave two competing models.
- Keep the intentional replacement of `ISubjectSource.PendingWriteCount` with `Diagnostics.OutboundRetries.Depth`.
- Make the `SubjectPropertyWriter` constructor internal. Connector authors receive the writer through `StartListeningAsync`; they do not need to construct one or supply its metrics.
- Keep one clear `SubjectSourceBase` constructor surface with the parameters required by custom sources. Do not add overloads solely to preserve the previous binary signature.
- Remove or narrow any new public member that exists only for built-in wiring or tests. Keep public members that form a useful, documented connector-authoring contract.
- Update public API snapshots only for intentional final API changes.

No compatibility alias or overload is required. A smaller, coherent final API takes priority over preserving an unreleased or unused shape.

## Simplification and Performance Review

The cleanup will inspect every changed production path, with these constraints:

- Do not replace sampled queue depth with eager per-item accounting.
- Do not allocate diagnostics or sub-diagnostics from getters.
- Keep allocation-producing compare-exchange snapshots on cold state transitions and error or drop paths, not on successful steady-state delivery.
- Remove private constructor chains when inherited metrics can supply the same counter instances directly and clearly.
- Remove comments that repeat the code. Keep comments that explain concurrency, lifecycle ordering, protocol semantics, or a non-obvious performance choice.
- Preserve tests for distinct failure modes. Consolidate fixtures or duplicated assertions only when the resulting test still fails for the original defect.
- Do not add counters or caches solely to optimize occasional diagnostic reads such as OPC UA session enumeration unless measurement shows that sampling is material.

If cleanup changes a steady-state enqueue, dequeue, property write, or message-delivery path, read `docs/benchmarking.md` and run the relevant comparison benchmark. Cold-path registration and lifecycle-only changes do not justify a benchmark by themselves.

## Connector Correctness Audit

For MQTT, OPC UA, and WebSocket clients and servers, verify:

- `IsOperational` rises only at the documented protocol-specific point.
- Every disconnect, teardown, failed attempt, stop, and disposal path drops liveness.
- Expected shutdown failures do not replace the sticky genuine `LastError`.
- Injected kills do not count as connector errors and cannot leak kill state into the next attempt.
- `StartTime` and every `Total` counter use the same hosted-service epoch.
- Recreated processors fold their drop counters exactly once.
- Diagnostics getters do not retain disposed per-attempt components as live state.

Unrelated behavior already present in the PR, including the WebSocket heartbeat and restart fixes and `ChangeQueueProcessor` capacity validation, stays in scope. The review may simplify their implementation but will not remove their behavior.

## Verification

Implementation will use regression tests for the two identified queue-registration failures before changing `QueueMetrics`:

- simultaneous registration attempts, exactly one succeeds;
- a stale handle cannot release a later registration.

After the cleanup:

1. Run focused tests for each changed component during development.
2. Run `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
3. Run the targeted OPC UA, MQTT, and WebSocket integration test projects because connector implementations changed.
4. Verify all affected public API snapshots.
5. Run `git diff --check` and confirm the worktree contains only intended changes.
6. Before claiming final merge readiness, run the Connector Tester scenarios for the affected connectors or record that the user declined the hours-long manual verification. Obtain explicit approval immediately before launching that long-running tool.

## Expected Result

PR #454 retains its shared diagnostic model and operational behavior. Its core registration API becomes smaller and linearizable, unnecessary public plumbing is removed, connector state semantics remain covered, and no new diagnostics work is added to steady-state message or property delivery.
