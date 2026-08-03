# Fallback attachment ownership in InterceptorExecutor

Design for #402. No public API change.

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

C's registration is gone with no exception and no caller error. Reachable wherever the same subject is mutated concurrently through `SubjectUpdateApplier`, `SubjectItemsUpdateApplier`, `OpcUaSubjectLoader`, `DynamicSubject` or `RootManager`.

### Defect 2: the double detach itself

Two threads can both pass `HasFallbackContext` and both run the detach callbacks. Benign today only because `LifecycleInterceptor` is the sole production `ILifecycleInterceptor` and its detach path happens to be idempotent under `lock (_attachedSubjects)`. Nothing in the contract requires that of a consumer implementation.

### Defect 3: a subject on a cyclic chain cannot be detached

The resolve runs before the removal. Since #400 a pure delegation cycle raises `InvalidOperationException` instead of overflowing the stack, so when the fallback's chain is such a cycle the resolve throws and `base.RemoveFallbackContext` is never reached. Detaching is the natural way to recover from a cycle and is exactly what does not work.

### Defect 4: the add path leaves a half-attached subject

The mirror. `base` commits, then the resolve throws, so the edge is registered, no attach callback ran, and the caller cannot tell how far it got.

### Defect 5: defect 3 also retains the subtree

`_usedByContexts` chains downward from the root, so a subject is collectable only once its fallback registration is actually removed. The aborted removal in defect 3 leaves the edge and the reverse entry in place.

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

Verified, not assumed. Inverting the remove path so `base` decides first, which is what #402's own Fix section proposes, fails exactly four tests in `Namotion.Interceptor.Tracking.Tests`: `WhenRemovingInterceptors_ThenAllChildrenAreDetached`, `WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached`, `WhenAssigningSubject_ThenAllSubjectsAreAttached` and `LifecycleEventsTests.SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler`. The first loses every detach event, because `SubjectRegistry` and `ContextInheritanceHandler` live on the parent context and become unreachable the moment the edge is gone. Inverting the add path fails 20 or more across `DerivedProperty*`, `ContextInheritanceHandler` and `WriteTimestamp`.

**#402's prescribed fix would therefore silently lose every detach event.** It must be struck from the issue so nobody re-proposes it.

Note the distinction the design depends on: the *resolve* of `ILifecycleInterceptor` reads the **parent's** chain and does not need the edge, while the *callbacks* read **this** executor's chain and do. So the resolve can move earlier even though the callbacks cannot.

## Design

Record what was attached, and make that record the ownership token for the edge. Give the token a phase, so a removal cannot overtake an attachment that has not finished.

### Where the record lives

On `InterceptorSubjectContext`, as a plain field guarded by `_mutationLock`, **not** in `ContextState`.

```csharp
private protected sealed class FallbackAttachment
{
    internal InterceptorSubjectContext Context = null!;
    internal ImmutableArray<ILifecycleInterceptor> Interceptors;
    internal bool IsAttached;              // false while the attach callbacks are still running
    internal FallbackAttachment? Next;
}

// Null for every context that is not an InterceptorExecutor. Read and written only under
// _mutationLock, and never on a resolution path.
private FallbackAttachment? _fallbackAttachments;
```

An earlier revision put a parallel `ImmutableArray` in `ContextState`. That is atomic but pays for it three ways: a phase change would need a state publish, every one of the six `ContextState` construction sites has to carry the array or ownership is silently erased, and a prototype that missed the two service mutators failed all eight `ContextConcurrencyFuzzTests` seeds. A plain field mutated under the same lock that publishes the topology is atomic with respect to that topology for anyone who takes the lock, which is every mutator, and it removes all three costs.

`ContextState`, `WithoutCaches`, `AddService` and `TryAddService` are untouched by this design.

### Phases

`IsAttached` is the whole state machine:

| state | meaning | a remove sees |
|---|---|---|
| node present, `IsAttached == false` | an add published the edge and its callbacks are still running | `false`, linearizing the remove before the add completes |
| node present, `IsAttached == true` | the add finished | takes the node and proceeds |
| node absent, edge present | a remove took it and has not published phase two | `false` |
| node absent, edge absent | nothing to do | `false` |

Removing the node *is* the claim, so no separate "detaching" state is needed: everything happens under `_mutationLock`, which serialises the claim without a CAS.

### Base API

Four `private protected` members, so they stay off the public surface and remain reachable from `InterceptorExecutor` in the same assembly. All four take `_mutationLock`.

```csharp
// Publishes the edge and inserts an unattached node, in one locked section. Null when the
// edge already exists. Performs the R4 _usedByContexts register-before-publish and the
// trailing InvalidateUsingContexts, exactly as AddFallbackContext does today.
private protected FallbackAttachment? TryBeginFallbackAttachment(
    IInterceptorSubjectContext context, ImmutableArray<ILifecycleInterceptor> interceptors);

// Marks the node attached. Must be called from a finally, so a throwing callback still
// leaves the edge removable.
private protected void CompleteFallbackAttachment(FallbackAttachment attachment);

// Phase one of removal: unlinks an attached node, leaves the edge. No publish, so no
// invalidation and no cache loss. False when absent or still attaching.
private protected bool TryTakeFallbackAttachment(
    IInterceptorSubjectContext context, out ImmutableArray<ILifecycleInterceptor> interceptors);

// Phase two: publishes the state without the edge and unregisters from _usedByContexts
// after the publish, preserving R4. Re-derives the index. No-op when already gone.
private protected void CompleteFallbackContextRemoval(IInterceptorSubjectContext context);
```

### The overrides

`InterceptorExecutor.cs` needs `using System.Collections.Immutable;` and `using System.Runtime.ExceptionServices;`, neither of which it has today.

```csharp
public override bool AddFallbackContext(IInterceptorSubjectContext context)
{
    // Preserves today's behaviour that a duplicate add neither resolves nor throws. Racy by
    // nature, and that is fine: TryBeginFallbackAttachment arbitrates.
    if (HasFallbackContext(context))
    {
        return false;
    }

    // Reads the parent's chain, so it does not need the edge. A throw here leaves nothing
    // committed, which is what closes defect 4.
    var interceptors = context.GetServices<ILifecycleInterceptor>();

    var attachment = TryBeginFallbackAttachment(context, interceptors);
    if (attachment is null)
    {
        return false;
    }

    try
    {
        for (var index = 0; index < interceptors.Length; index++)
        {
            interceptors[index].AttachSubjectToContext(_subject);
        }
    }
    finally
    {
        // From a finally: a throwing attach callback deliberately leaves the edge committed,
        // and an edge whose node never became attached could never be removed.
        CompleteFallbackAttachment(attachment);
    }

    return true;
}

public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
{
    // Taking the node is the arbiter. The edge is deliberately still in place, because the
    // callbacks below resolve their handlers through it.
    if (!TryTakeFallbackAttachment(context, out var interceptors))
    {
        return false;
    }

    ExceptionDispatchInfo? failure = null;
    try
    {
        // Best effort: the node is already gone, so an interceptor skipped here could never
        // be balanced by a later removal.
        for (var index = 0; index < interceptors.Length; index++)
        {
            try
            {
                interceptors[index].DetachSubjectFromContext(_subject);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
    finally
    {
        CompleteFallbackContextRemoval(context);
    }

    failure?.Throw();
    return true;
}
```

### Why the state agrees once writes settle

AGENTS.md ranks quiescent consistency under correctness, so an end state where the edge is absent while `_attachedSubjects` says attached is a defect, not an accepted anomaly. The phase is what rules it out.

Without the phase, this diverges:

```
C: publish edge and record
A: take record
A: detach callbacks run, no-op because C has not attached yet
A: remove edge
C: attach callbacks run
```

Edge absent, subject attached. With the phase, A sees `IsAttached == false` and returns `false`, so it cannot take a node whose attach is still in flight. Every interleaving then reduces to one of:

- **the add wins the edge**: a concurrent remove returns `false` while attaching, or takes the finished node and removes cleanly afterwards
- **the remove wins the edge**: a concurrent add sees the edge present and returns `false` without attaching

In both, topology and bookkeeping agree at quiescence. The residual `false` returns are legal linearizations: a remove that returns `false` during an add's attach phase orders before that add completed, and a remove that returns `false` because another remover holds the claim orders after that remover.

The phase also closes the reentrancy hole a previous revision documented as unfixable: a handler that calls `RemoveFallbackContext` for the same context from inside an attach callback now sees `IsAttached == false` and returns `false`, instead of removing the edge out from under the loop that is still attaching.

### How each defect closes

| # | Defect | Outcome |
|---|---|---|
| 1 | Remove racing add undoes a live registration | **Closed.** The claim is atomic under `_mutationLock`, and the phase prevents a removal from overtaking an unfinished attach, so no interleaving leaves topology and bookkeeping disagreeing at quiescence. |
| 2 | Double detach | **Closed.** One node, one taker, so the callbacks run once by construction rather than by relying on `LifecycleInterceptor` being idempotent. |
| 3 | Cyclic chain blocks detach | **Closed** for removability. No resolve on the detach path, and phase two is in the `finally`. See the caveat below on what still throws. |
| 4 | Half-attached subject on the add path | **Closed.** The resolve precedes the commit, so a throw leaves no edge and no attach. |
| 5 | Retained subtree | **Closed for the `_usedByContexts` edge only.** See below. |

### Defect 5 is narrower than it looks

The edge and its reverse `_usedByContexts` entry always come out, so the retention this design targets is gone. That is not the same as the subtree becoming collectable.

`LifecycleInterceptor._attachedSubjects` (`:10`) and `SubjectRegistry._knownSubjects` (`SubjectRegistry.cs:10`) both strongly retain subjects, and both are cleared only by notifications that travel through `context.GetServices<ILifecycleHandler>()`. On a cyclic chain that resolve throws, so neither is cleared. `DetachSubjectFromContext` processes children before the subject itself (`:68-73`), so the throw aborts before `DetachFromContext` runs at all. Measured:

```
childCount = 0:  threw / fallbacks after = 0 / _attachedSubjects = 0 / retained: []
childCount = 2:  threw / fallbacks after = 0 / _attachedSubjects = 2 / retained: [CC1, S1]
```

Best-effort detach across recorded interceptors does not help here: the failure is inside a single interceptor, and the handlers it needs are unreachable from every context on the loop, so no route exists.

So the claim is: **the fallback edge and its reverse entry always come out; general collectability holds for the acyclic case.** Test 5 is scoped accordingly, and the remainder is handed to #384, whose subject is exactly this.

### Caveat on defect 3: when removing a cyclic edge throws

The recorded `LifecycleInterceptor.DetachSubjectFromContext` resolves its handlers through `subject.Context` (`LifecycleInterceptor.cs:70,73,195,278`), which is this executor. When that chain is a cycle, the callback throws and the exception surfaces after the edge has been removed.

The shape matters, and the obvious one does not exhibit it. On a *pure two-executor cycle* both records are empty, because each was resolved while the other end was still empty, so the detach loop runs zero times and the removal succeeds cleanly. To see the throw the record must have been captured non-empty and the chain must have become cyclic afterwards:

```
e2 -> root (WithFullPropertyTracking)
e1 -> e2                                 e1's record captures [LifecycleInterceptor]
drop e2 -> root
add e2 -> e1                             the chain is now a cycle
e1.RemoveFallbackContext(e2)             throws, and the edge is gone
```

Test 3 must build exactly this. Written against the pure two-executor cycle it would assert a throw that does not happen.

### Undeclared behaviour change: an add that closes a cycle now succeeds

Today, adding the edge that closes a pure delegation cycle throws, because the edge is committed first and the subsequent resolve sees the loop. After this change the resolve runs first, before the loop exists, and a pure cycle requires every context on it to have no services and exactly one fallback, so the resolving end is empty and returns `Empty`. The callback loop has zero iterations and nothing throws.

```
today:  e2.Add(e1) = True, then e1.Add(e2) THREW InvalidOperationException
after:  e2.Add(e1) = True, then e1.Add(e2) = True, edge present, no throw
```

This is a real behaviour change and belongs in the release notes alongside the attach/detach symmetry change below. It is defensible: the caller asked for an edge and got one, and the resulting cycle is now detectable on the next resolution and recoverable by removal, which it was not before.

### Exception contract

The resolve on the add path can throw for three reasons, not two: a delegation cycle, a consumer's `Equals` or `GetHashCode` during the service walk's dedup, and circular ordering dependencies from `ServiceOrderResolver` (`:190`, `"Circular dependency detected in service ordering"`).

- **An add whose pre-commit resolution fails leaves no trace.** Nothing is published, no node is created.
- **An add whose attach callbacks fail leaves the edge committed and the node attached**, so it stays removable. That is deliberate, and it is #384's rollback problem, not this one's.
- **A remove always removes**, and reports the first callback failure after every recorded interceptor has been given its detach.

The asymmetry is the point: a blocked removal is what strands edges and retains subtrees, so removal must never be blocked by a handler failure.

### Behaviour change: attach and detach become symmetric

Detach now notifies exactly the interceptors that were *resolved* at attach time. Today the set is resolved fresh at detach time, so an `ILifecycleInterceptor` registered on the parent after the attach receives a detach it never saw an attach for, and one unregistered in between misses its detach. Measured: a late-registered interceptor sees `detaches=1, attaches=0` today and `0/0` after.

"Resolved at attach time" and not "notified at attach time": if an attach callback throws at index k, the interceptors after it never received an attach, yet a later remove still detaches all of them. That case continues to rely on consumer detach idempotency. Closing it is #384's rollback problem.

## Interaction with #400

The design no longer touches `ContextState`, so #400's invariants are untouched by construction: the never-installed-twice property, `WithoutCaches`, the `DelegationTarget` derivation and the resolution and invalidation paths all see exactly what they see today. R4 is preserved because the two publishing members reuse the existing register-before-publish and unregister-after-publish ordering. Phase one publishes nothing, so it has no R4 obligation and cannot perturb a concurrent walk's state identity.

## Performance

Removal keeps **one** publish, as today. Phase one is a lock acquisition and a list unlink, with no publish, no allocation and no invalidation walk. This is what an earlier `ContextState`-based revision got wrong: putting the record in the state forced phase one to publish, which invalidated the entire upward cone and destroyed the caches immediately before the detach callbacks used them. Measured on that prototype, chain of N subjects, Release, best of 3 by 20 iterations:

| depth | today | record in `ContextState` | this design |
|---|---|---|---|
| 200 | 6.8 ms | 25.5 ms | one publish, expected near baseline |
| 400 | 14.5 ms | 128.2 ms | one publish, expected near baseline |

The right-hand column is a prediction and must be measured. `ContextInheritanceHandler.cs:25` calls `RemoveFallbackContext` once per subject during a subtree detach, so any per-call regression multiplies across the graph.

Add pays one extra lock acquisition for the phase transition. Both paths lose the detach-time `GetServices<ILifecycleInterceptor>()`.

Memory: one reference field per context, 8 bytes, plus one `FallbackAttachment` per attached edge, 48 bytes on x64. For the dominant single-edge subject that is 56 bytes. The stored `ImmutableArray` is the instance the parent context already caches, so it is a pointer copy and not a second allocation.

`RegistryBenchmark` is the gate, read for both allocation and the detach path. AGENTS.md ranks allocations above CPU, so the 48 bytes per edge needs a number, not a prediction.

## Testing

Defects:

1. `WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce`
2. `WhenRemoveRacesAdd_ThenTopologyAndBookkeepingAgree`. Named for what is guaranteed, not for "does not undo the add"
3. `WhenChainBecomesCyclicAfterAttach_ThenTheEdgeIsRemovedAndTheCallThrows`, built with the construction above, not on a pure two-executor cycle
4. `WhenAttachResolveThrows_ThenNoEdgeIsRegistered`, with `WhenAddClosesADelegationCycle_ThenTheEdgeIsRegisteredAndNothingThrows` pinning the declared behaviour change
5. `WhenAcyclicSubtreeIsDetached_ThenItBecomesCollectable`, by weak reference probe, scoped to the acyclic case

Forced orderings, so a future refactor cannot reintroduce the inversion:

6. `WhenFallbackIsAdded_ThenAttachCallbacksSeeTheEdge`
7. `WhenFallbackIsRemoved_ThenDetachCallbacksStillSeeTheEdge`

Semantic change:

8. `WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach`

The phase machine, which is new and unprototyped, so it carries the most risk:

9. `WhenRemoveRunsDuringAttachCallbacks_ThenItReturnsFalseAndTheEdgeSurvives`. Pause the add between publishing and its callbacks, run a remove to completion, release the add, assert topology and bookkeeping agree
10. `WhenRemoveRacesBetweenTwoAttachCallbacks_ThenNoInterceptorStaysAttachedWithoutAnEdge`. Pause between interceptors
11. `WhenDetachInterceptorThrows_ThenLaterInterceptorsStillReceiveDetach`
12. `WhenCyclicSubtreeWithChildrenIsDetached_ThenRetentionIsRecorded`, pinning the measured `_attachedSubjects` remainder so #384 has a failing case to fix

The existing `LifecycleInterceptorTests` snapshots stay untouched. If any move, the change is wrong. `ContextConcurrencyFuzzTests` must be run: it is the only existing suite that catches ownership regressions, and `ContextSubtreeServiceTests` does not, verified by dropping the carries in an earlier prototype and watching it pass 3/3.

`ContextConcurrencyFuzzTests` needs one model change: `BuildTopology:460-469` catches the delegation-cycle exception during setup and leaves the edge recorded present, with a comment stating "the topology stays exactly as declared". Under this design a failed pre-commit resolve leaves no edge, so the model must record absence. The `RunOperation` site at `:519-527` was previously thought to be the one needing the change; it is not sufficient on its own, measured at 5 of 8 seeds still failing.

Conventions per AGENTS.md: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, no hardcoded waits, `CountdownEvent` plus `ManualResetEventSlim` rendezvous and `AsyncTestHelpers.WaitUntilAsync` as in `ContextFunctionCacheTests`.

Tests 1, 2, 9 and 10 must be verified by mutation: remove the phase check, remove the claim, and confirm each fails.

## Scope

Four files:

- `src/Namotion.Interceptor/InterceptorSubjectContext.cs`: the `FallbackAttachment` nested type, the `_fallbackAttachments` field, and the four `private protected` members
- `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`: the two overrides and two missing usings
- `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs`: the `BuildTopology` model change
- one new test file, which can live in `Namotion.Interceptor.Tests` since it reaches Tracking transitively through `Namotion.Interceptor.Testing`

Not touched: `ContextState` and its construction sites, `ContextInheritanceHandler.cs`, `LifecycleInterceptor.cs`, `ContextStateReflection.cs`.

No public API change expected: `InterceptorExecutor` is sealed, the overrides exist, and `private protected` members and nested types are not public surface. Confirm with `VerifyChecksTests.PublicApi` rather than assuming.

`ExceptionDispatchInfo` is available on netstandard2.0.

## Issue outcome

#402 closes on merge, with its text amended first, because parts of it are wrong and one requirement is not met:

- **strike the Fix section.** "Make `base` the arbiter, and resolve after it" loses every detach event, and its boldface claim that the original objection was wrong is itself wrong
- **defect 3**: restate the criterion as "the edge is always removed", since the call can still throw
- **defect 5 and Tests item 5**: scope to the acyclic case, and hand the `_attachedSubjects` and `_knownSubjects` retention to **#384** with the measurement above
- **Tests item 2**: rename away from "a remove racing an add does not undo the add" to what is guaranteed
- **Scope / #207**: invoke #402's own sanctioned split, and note in **#207** that the removal primitive it needs now exists

No new issues. The only remainder, defect 5's cyclic retention, lands in #384, which already covers exception-driven reconciliation corruption and whose existing comment about a throwing add is delivered by this design.
