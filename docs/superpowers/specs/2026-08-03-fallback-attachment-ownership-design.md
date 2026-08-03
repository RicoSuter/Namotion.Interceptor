# Fallback attachment ownership in InterceptorExecutor

Design for #402. Closes all five defects listed there without new public API and without a follow-up issue.

## Problem

`InterceptorExecutor` overrides both fallback mutators so that changing the topology also fires the lifecycle attach and detach callbacks. Both overrides re-derive at detach time what they already knew at attach time: they resolve `ILifecycleInterceptor` from the fallback context again, from a chain that may have changed or broken in between. Every defect in #402 follows from that.

```csharp
public override bool AddFallbackContext(IInterceptorSubjectContext context)
{
    var result = base.AddFallbackContext(context);          // commit
    if (result)
    {
        var array = context.GetServices<ILifecycleInterceptor>();   // resolve, can throw
        // AttachSubjectToContext for each
    }
    return result;
}

public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
{
    if (HasFallbackContext(context))                        // check, lock free
    {
        var array = context.GetServices<ILifecycleInterceptor>();   // resolve, can throw
        // DetachSubjectFromContext for each
        return base.RemoveFallbackContext(context);         // commit
    }
    return false;
}
```

### Defect 1: a remove racing an add silently undoes a live registration

`RemoveFallbackContext` checks and commits non-atomically and runs the callbacks in the gap:

```
A: HasFallbackContext -> true      A is committed to detaching, has not yet
C: RemoveFallbackContext           C detaches and removes
C: AddFallbackContext              C adds and re-attaches
A: DetachSubjectFromContext        the subject is attached again, so this detaches for real
A: base.RemoveFallbackContext      removes the fallback C just added
```

C's registration is gone with no exception and no caller error. Idempotency does not help, because the re-attach in between makes A's detach a real one. Reachable wherever the same subject is mutated concurrently through `SubjectUpdateApplier`, `SubjectItemsUpdateApplier`, `OpcUaSubjectLoader`, `DynamicSubject` or `RootManager`.

### Defect 2: the double detach itself

Two threads can both pass `HasFallbackContext` and both run the detach callbacks. Benign today only because `LifecycleInterceptor` is the sole production `ILifecycleInterceptor` and its detach path happens to be idempotent under `lock (_attachedSubjects)`. Nothing in the contract requires that of a consumer implementation.

### Defect 3: a subject on a cyclic chain cannot be detached

The resolve runs before the removal. Since #400 a pure delegation cycle raises `InvalidOperationException` instead of overflowing the stack, so when the fallback's chain is such a cycle the resolve throws and `base.RemoveFallbackContext` is never reached. Detaching is the natural way to recover from a cycle and is exactly what does not work.

### Defect 4: the add path leaves a half-attached subject

The mirror. `base` commits, then the resolve throws, so the edge is registered, no attach callback ran, and the caller cannot tell how far it got.

### Defect 5: defect 3 also retains the subtree

`_usedByContexts` chains downward from the root, so a subject is collectable only once its fallback registration is actually removed. The aborted removal in defect 3 leaves the edge and the reverse entry in place, so the detached subtree stays reachable for the lifetime of the graph.

## Two orderings that are forced, not accidental

Both callbacks resolve their handlers through the *subject's own* context:

```
interceptor.Attach|DetachSubjectFromContext(_subject)
  -> LifecycleInterceptor uses subject.Context, which IS this executor
    -> Invoke{Added,Removed}LifecycleHandlers
      -> this.GetServices<ILifecycleHandler>()
```

A generated subject's executor has no services of its own, so that resolve returns nothing unless the fallback edge is in place. Therefore:

- **attach callbacks must run after the edge is committed**
- **detach callbacks must run before the edge is removed**

This was verified, not assumed. Inverting the remove path so `base` decides first, which is what #402 originally proposed, fails four tests in `Namotion.Interceptor.Tracking.Tests`. `WhenRemovingInterceptors_ThenAllChildrenAreDetached` loses every detach event:

```
Verified (expected)                          Received (with the inversion)
[                                            [
  + NA (attached, refs: 0),                    + NA (attached, refs: 0),
  + Mother1.Mother -> Mother2 (attached),      + Mother1.Mother -> Mother2 (attached),
  + Mother2.Mother -> Mother3 (attached),      + Mother2.Mother -> Mother3 (attached)
  - Mother1.Mother -> Mother2 (detached),    ]
  - Mother2.Mother -> Mother3 (detached),
  - Mother1 (detached, refs: 0)
]
```

`SubjectRegistry` and `ContextInheritanceHandler` live on the parent context and become unreachable the moment the edge is gone, so nothing is detached and nothing is unregistered.

Any future change to these overrides has to preserve both orderings. They are pinned by the regression tests in the Testing section.

## Design

Stop re-deriving. Record what was attached, and make that record the sole ownership token for the edge.

```csharp
// Add: base arbitrates the edge. The record is written even when the resolve throws,
// so the edge stays removable afterwards.
if (!base.AddFallbackContext(context))
{
    return false;
}

// Empty and not default: a default ImmutableArray<T> throws on .Length, and this value is
// what a later remove reads back when the resolve below throws.
var interceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
try
{
    interceptors = context.GetServices<ILifecycleInterceptor>();
}
finally
{
    StoreAttachment(context, interceptors);
}

for (var index = 0; index < interceptors.Length; index++)
{
    interceptors[index].AttachSubjectToContext(_subject);
}

return true;

// Remove: taking the record arbitrates. Exactly one thread can take it, and the edge
// is still in place while the callbacks run.
if (!TryTakeAttachment(context, out var interceptors))
{
    return false;
}

try
{
    for (var index = 0; index < interceptors.Length; index++)
    {
        interceptors[index].DetachSubjectFromContext(_subject);
    }
}
finally
{
    base.RemoveFallbackContext(context);
}

return true;
```

### Exception contract on both paths

Neither `try` swallows. On the add path a throwing resolve propagates to the caller with the edge registered and the attachment recorded as empty, so the caller learns the attach did not complete and the edge is still removable. On the remove path a throwing callback propagates with the edge already removed, because `base.RemoveFallbackContext` runs in the `finally`. Removal must never be blocked by a handler failure, which is the same principle #384 argues for elsewhere.

### How each defect closes

| # | Defect | Closed by |
|---|---|---|
| 1 | Remove racing add undoes a live registration | The interlocked take is the single arbiter. A losing thread returns `false` and never reaches `base`, so it cannot destroy the winner's edge. |
| 2 | Double detach | Exactly one thread takes the record, so the callbacks run once by construction rather than by relying on `LifecycleInterceptor` being idempotent. |
| 3 | Cyclic chain blocks detach | The executor no longer resolves on the detach path, so nothing can throw *before* the removal is guaranteed. The removal itself is in the `finally` and always runs. See the caveat below: the call can still throw *after* that point. |
| 4 | Half-attached subject on the add path | The `finally` records the attachment even when the resolve throws, so the edge never becomes unremovable. |
| 5 | Retained subtree | Follows from 3: the edge and its reverse `_usedByContexts` entry always come out. |

### The record must be one atomically swapped unit

An earlier revision of this design stored the context and its interceptor array in two separate fields and put a CAS on the context field alone. That is not atomic and cannot be made atomic, because two concurrent adds of *different* contexts both pass `base.AddFallbackContext` and then race for the same slot:

- writing the interceptors before the CAS pairs the loser's array with the winner's context, so a later remove detaches the wrong set
- writing them after the CAS lets a taker read in between and observe a default `ImmutableArray`, whose `.Length` throws, or the stale array of a previous occupant

Reading the array before the CAS does not fix it either: an edge that is removed and re-added is ABA on the context field, so the CAS can succeed against a stale pairing. The context and its interceptors must move together in a single interlocked operation.

### Caveat on defect 3: removing a cyclic edge still throws

Removing the executor's own resolve is necessary but not sufficient. The recorded `LifecycleInterceptor.DetachSubjectFromContext` resolves its handlers through `subject.Context` (`LifecycleInterceptor.cs:70,73,195,278`), which is this executor, whose chain is the cycle. So the callback throws mid-loop, leaving `_attachedSubjects` and the reference counts partially updated.

What the design does guarantee is that the edge comes out anyway, via the `finally`, and that the graph is therefore recoverable. What it does not do is make the call succeed cleanly. `RemoveFallbackContext` on a cyclic chain **throws to the caller with the edge removed**. That is strictly better than today, where it throws with the edge still in place and the subtree retained, but it must be stated on the override and asserted that way in test 3 rather than left to the implementer to guess.

### Accepted anomalies

A remove and an add racing on the same edge can end with the add having returned `false` while the edge ends up absent:

```
A: TryTakeAttachment -> wins
C: AddFallbackContext -> base sees the edge still present -> returns false, no attach
A: detach callbacks, base.RemoveFallbackContext -> edge gone
```

C read `false` as "already present" and the edge is gone. This is a legitimate linearization of two concurrent mutations, and unlike today's behaviour the topology and the lifecycle bookkeeping still agree afterwards.

Two more, of the same kind. All three are documented on the overrides rather than fixed.

**A remove during an in-flight add returns `false` and leaves the edge.** Between `base.AddFallbackContext` committing and `StoreAttachment` running, the edge exists but no record does, so a concurrent remove finds nothing to take. Today it would have removed the edge. The window is the resolve in between, which can walk a chain, so it is not negligible.

**A detach callback that throws mid-loop loses the remaining callbacks permanently.** The record is already consumed and the `finally` removes the edge, so there is nothing left to retry against. Today the edge survives a throw, so a caller can retry and the idempotent callbacks converge. This is the deliberate trade for guaranteeing removability, and it is the same trade #384 argues for.

### Storage

An immutable singly linked list in one field, swapped with `Interlocked.CompareExchange`. This handles one edge and many edges uniformly, so there is no fast path plus overflow split, and no lock at all.

```csharp
private sealed class Attachment(
    IInterceptorSubjectContext context,
    ImmutableArray<ILifecycleInterceptor> interceptors,
    Attachment? next)
{
    public readonly IInterceptorSubjectContext Context = context;
    public readonly ImmutableArray<ILifecycleInterceptor> Interceptors = interceptors;
    public readonly Attachment? Next = next;
}

private Attachment? _attachments;
```

- `StoreAttachment` is a CAS loop that prepends a new node.
- `TryTakeAttachment` is a CAS loop that rebuilds the list without the node for the requested context, and returns `false` when that context is not in the list. Two threads taking the same context both rebuild, one CAS wins, the loser retries, no longer finds the context, and returns `false`. So exactly one taker wins, which is the property the whole design rests on.
- Nodes are immutable and rebuilt rather than mutated, so an unlinked node is unreachable and cannot retain the parent's interceptors after a detach.

Removing the overflow list also removes its lock, so the leaf-lock question and its interaction with the lock order at the top of `InterceptorSubjectContext` and with `lock (_attachedSubjects)` in `LifecycleInterceptor` disappears rather than needing an argument.

For context on list length: the dominant shape is exactly one fallback context per executor, since a subject constructed without a context gets one inherited edge and `ContextInheritanceHandler` adds an edge only on the first reference (`ReferenceCount: 1, IsContextAttach: true`), so a second parent adds nothing. Two edges occur when a subject is constructed with an explicit context and then placed under a parent, or when a caller uses the public API directly. The list is walked only on add and remove, never on a resolution path.

### Reentrancy

These callbacks normally run with `lock (_attachedSubjects)` already held by `LifecycleInterceptor` further up the stack, and `Monitor` is reentrant, so a handler can call back into these overrides on the same thread. The relevant case is a handler that calls `RemoveFallbackContext` for the same context from inside an attach callback: the record is already stored, so the take succeeds, the edge is removed, and the outer add then continues attaching the remaining interceptors against an edge that no longer exists.

This is not made worse by the design, and the analogous hole exists today. It is called out because the design's exactly-once argument is otherwise easy to misread as covering it: it covers concurrent threads, not a handler reentering on the same one. No guard is proposed. A handler that mutates the topology it is being notified about is outside the contract, which is the same position `LifecycleInterceptor` already documents for handlers writing the property being reconciled.

### Behaviour change: attach and detach become symmetric

Detach now notifies exactly the interceptors that were *resolved* at attach time. Today the set is resolved fresh at detach time, so an `ILifecycleInterceptor` registered on the parent after the attach receives a detach it never saw an attach for, and one unregistered in between misses its detach. The new pairing is balanced by construction. This is deliberate and is the defensible semantic, but it is a behaviour change and belongs in the release notes.

"Resolved at attach time" and not "notified at attach time": if an attach callback throws at index k, the interceptors after it never received an attach, yet a later remove still detaches all of them from the record. So the exception case continues to rely on consumer detach idempotency, which defect 2 correctly notes the contract does not require. Closing that would mean recording how far the attach loop got, which is #384's rollback problem and is out of scope here.

### What remains, and why it is not a follow-up

In a pure delegation cycle every context on the loop resolves nothing, so the lifecycle handlers are unreachable from every route, not just from the one we happened to pick. The edge is still removed and the graph still recovers, but the registry bookkeeping for that subtree stays stale until it is rebuilt. Breaking the cycle first does not help: that is the inversion shown above, which leaves the executor with nothing to resolve.

The route that produces this shape in practice is the stranded-detach path tracked by #410, which is already open. A cycle built deliberately through the public API is a programming error, and "removable, recoverable, with stale registry entries until re-attach" is the right level of service for it. This gets documented on the overrides, not filed.

## Performance

The remove path loses two operations and gains one:

| | today | after |
|---|---|---|
| `HasFallbackContext` | `Volatile.Read` plus `ImmutableArray.Contains` scan | gone |
| `GetServices<ILifecycleInterceptor>` | delegation target resolve plus cache lookup | gone |
| take the record | none | one `Interlocked.CompareExchange` |

The add path is neutral: the same resolve, plus one store.

The cost is memory, and it is larger than a field-only scheme would have been. Per executor: one reference field, 8 bytes. Per fallback edge: one `Attachment` node, 40 bytes on x64 (16 header, plus `Context`, the one-reference `ImmutableArray` struct, and `Next`). A subject with the dominant single edge therefore costs 48 bytes, or roughly 480 KB for a 10,000 node graph. The `ImmutableArray` itself is the instance the parent context already caches, so it is a pointer copy and not a second allocation.

That is the price of making the pairing atomic, and it is not negotiable: the two-field scheme that would have cost 24 bytes is incorrect, as shown above.

This must be measured, not assumed, and it is the one part of this design that could force a rethink. `RegistryBenchmark` is the relevant one because it is attach heavy. The removed `GetServices` call on every detach offsets some of it, but that is a prediction. Treat a material regression there as a gate: if it does not hold, the fallback option is to intern the node for the common single-edge case rather than to reintroduce the unsound two-field split.

## Testing

Regression tests for the defects:

1. `WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce` (defect 2)
2. `WhenRemoveRacesAdd_ThenTheAddIsNotUndone` (defect 1), asserting that the edge and the lifecycle bookkeeping agree afterwards
3. `WhenChainIsDelegationCycle_ThenTheEdgeIsRemovedAndTheCallThrows` (defect 3). Asserts both halves: the `InvalidOperationException` reaches the caller *and* `HasFallbackContext` is false afterwards. Asserting a clean `true` would be wrong, see the caveat above.
4. `WhenAttachResolveThrows_ThenTheEdgeRemainsRemovable` (defect 4)
5. `WhenCyclicSubtreeIsDetached_ThenItBecomesCollectable` (defect 5), by weak reference probe

Guards for the two forced orderings, so a future refactor cannot silently reintroduce the inversion:

6. `WhenFallbackIsAdded_ThenAttachCallbacksSeeTheEdge`
7. `WhenFallbackIsRemoved_ThenDetachCallbacksStillSeeTheEdge`

And for the deliberate semantic change:

8. `WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach`

And for the storage, since its correctness is the whole design and a plain functional test will not exercise the race:

9. `WhenDifferentFallbacksAreAddedConcurrently_ThenEachRecordKeepsItsOwnInterceptors`. This is the case that killed the two-field scheme, so it must fail against that scheme and pass against the linked list. Verify by mutation, not by passing once.

The existing `LifecycleInterceptorTests` snapshots stay untouched. If any of them move, the change is wrong.

Conventions per AGENTS.md: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, and no hardcoded waits. Concurrency tests use `CountdownEvent` plus `ManualResetEventSlim` rendezvous and `AsyncTestHelpers.WaitUntilAsync`, matching `ContextFunctionCacheTests`.

## Scope

One production file, `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`. Roughly +60 and -25 lines.

Not touched: `InterceptorSubjectContext.cs`, `ContextInheritanceHandler.cs`, `LifecycleInterceptor.cs`.

No public API change. `InterceptorExecutor` is sealed, the two overrides already exist, and the new field and nested type are private, so `VerifyChecksTests.PublicApi.verified.txt` does not move. No new exception type is needed, because the design removes the executor's own resolve on the detach path.

`InterceptorSubjectContext.HasFallbackContext` loses its only caller. Leave it: it is `protected` on a public unsealed class, so removing it would be a breaking change for a consumer subclass, and it is still the natural way to ask the question. Tests 3 and 7 use it.

Out of scope: #410 (stranded fallback edges on a partial detach), #207 (constructor context leak), #404 (factory under the mutation lock), #405 (invalidation walk recursion). None of them touch this file.
