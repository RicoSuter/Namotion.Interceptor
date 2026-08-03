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
| 3 | Cyclic chain blocks detach | There is no resolve on the detach path, so nothing can throw before the removal. |
| 4 | Half-attached subject on the add path | The `finally` records the attachment even when the resolve throws, so the edge never becomes unremovable. |
| 5 | Retained subtree | Follows from 3: the edge and its reverse `_usedByContexts` entry always come out. |

### Why the record can be read after the take

`_attachedInterceptors` is written only by a thread whose `base.AddFallbackContext` returned `true`, meaning it moved the edge from absent to present. A taker reads the record while the edge is still present and is the only thread that can remove it, and it does so after the read. So no second writer can appear in the window between the take and the read.

### The one accepted anomaly

A remove and an add racing on the same edge can end with the add having returned `false` while the edge ends up absent:

```
A: TryTakeAttachment -> wins
C: AddFallbackContext -> base sees the edge still present -> returns false, no attach
A: detach callbacks, base.RemoveFallbackContext -> edge gone
```

C read `false` as "already present" and the edge is gone. This is a legitimate linearization of two concurrent mutations, and unlike today's behaviour the topology and the lifecycle bookkeeping still agree afterwards. Documented on the override rather than fixed.

### Storage

The dominant shape is exactly one fallback context per executor: a subject constructed without a context gets one inherited edge, and `ContextInheritanceHandler` adds an edge only on the first reference (`ReferenceCount: 1, IsContextAttach: true`), so a second parent adds nothing. Two edges occur when a subject is constructed with an explicit context and then placed under a parent, or when a caller uses the public API directly.

```csharp
private IInterceptorSubjectContext? _attachedContext;
private ImmutableArray<ILifecycleInterceptor> _attachedInterceptors;
private List<(IInterceptorSubjectContext Context, ImmutableArray<ILifecycleInterceptor> Interceptors)>? _additionalAttachments;
```

- `StoreAttachment` first tries `Interlocked.CompareExchange(ref _attachedContext, context, null)`. On failure it falls back to `_additionalAttachments` under that list's own lock, creating the list on demand.
- `TryTakeAttachment` compares against `_attachedContext` and takes it with a CAS back to `null`, otherwise scans `_additionalAttachments` under the lock.
- The same context can never be in both, because two concurrent adds of one context cannot both pass `base.AddFallbackContext`.

`_additionalAttachments` and its lock are a leaf: nothing is invoked while it is held, and in particular no callback runs under it. This keeps it outside the lock order noted at the top of `InterceptorSubjectContext`.

### Behaviour change: attach and detach become symmetric

Detach now notifies exactly the interceptors that attach notified. Today the set is resolved fresh at detach time, so an `ILifecycleInterceptor` registered on the parent after the attach receives a detach it never saw an attach for, and one unregistered in between misses its detach. The new pairing is balanced by construction. This is deliberate and is the defensible semantic, but it is a behaviour change and belongs in the release notes.

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

The cost is memory. Two reference-sized fields on every `InterceptorExecutor`, so 16 bytes per subject on x64, or 160 KB for a 10,000 node graph. The stored `ImmutableArray` is the instance the parent context already caches, so recording it is a pointer copy and not an allocation. `_additionalAttachments` allocates only for subjects with more than one fallback edge.

This must be measured, not assumed. `RegistryBenchmark` is the relevant one because it is attach heavy. The expectation is that the removed `GetServices` call offsets the field cost, but that is a prediction.

## Testing

Regression tests for the defects:

1. `WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce` (defect 2)
2. `WhenRemoveRacesAdd_ThenTheAddIsNotUndone` (defect 1), asserting that the edge and the lifecycle bookkeeping agree afterwards
3. `WhenChainIsDelegationCycle_ThenSubjectCanStillBeDetached` (defect 3)
4. `WhenAttachResolveThrows_ThenTheEdgeRemainsRemovable` (defect 4)
5. `WhenCyclicSubtreeIsDetached_ThenItBecomesCollectable` (defect 5), by weak reference probe

Guards for the two forced orderings, so a future refactor cannot silently reintroduce the inversion:

6. `WhenFallbackIsAdded_ThenAttachCallbacksSeeTheEdge`
7. `WhenFallbackIsRemoved_ThenDetachCallbacksStillSeeTheEdge`

And for the deliberate semantic change:

8. `WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach`

The existing `LifecycleInterceptorTests` snapshots stay untouched. If any of them move, the change is wrong.

Conventions per AGENTS.md: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, and no hardcoded waits. Concurrency tests use `CountdownEvent` plus `ManualResetEventSlim` rendezvous and `AsyncTestHelpers.WaitUntilAsync`, matching `ContextFunctionCacheTests`.

## Scope

One production file, `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`. Roughly +60 and -25 lines.

Not touched: `InterceptorSubjectContext.cs`, `ContextInheritanceHandler.cs`, `LifecycleInterceptor.cs`.

No public API change. `InterceptorExecutor` is sealed, the two overrides already exist, and the new fields are private, so `VerifyChecksTests.PublicApi.verified.txt` does not move. No new exception type is needed, because the design removes the resolve that would have thrown.

Out of scope: #410 (stranded fallback edges on a partial detach), #207 (constructor context leak), #404 (factory under the mutation lock), #405 (invalidation walk recursion). None of them touch this file.
