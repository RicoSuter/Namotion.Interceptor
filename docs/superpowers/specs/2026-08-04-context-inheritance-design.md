# Context inheritance as an owned parent link

Design document. Written 2026-08-04 against `master` at `e616c769`.

## 1. Problem

`IInterceptorSubjectContext.AddFallbackContext` does three unrelated jobs under one name, and the
conflation is the common cause behind an issue cluster that is not converging.

1. **Service composition.** Add a context to a subject's resolution chain. This is what the name says.
2. **Root entry.** Attach a subject and its whole subtree to a lifecycle graph. `RootManager`, the
   generated constructor and `DynamicSubject` use it this way.
3. **Subtree descent.** `ContextInheritanceHandler` calls it from inside a lifecycle callback, and the
   re-entry into `InterceptorExecutor.AddFallbackContext` is what discovers the next level of the
   object graph.

Whether a given call does job 2 or 3 is decided by the contents of the context being added, not by the
call site, because `InterceptorExecutor` resolves `ILifecycleInterceptor` from it. The same line
therefore means different things depending on what somebody registered elsewhere.

Nine open issues live in this area. Six of them are the same shape: the library runs user-supplied code
in the middle of a multi-step state transition. `TryAddService` runs a factory under the mutation lock
(#403, #404, #406), `LifecycleInterceptor` runs handlers mid-reconciliation (#384), and
`InterceptorExecutor` runs lifecycle interceptors between publishing an edge and owning it (#402, with
#411 as the window those callbacks force open). The remaining two, #207 and #410, are the derived-graph
problem: the object graph and the context graph are kept in step imperatively by a handler reacting to
lifecycle events.

Every fix so far has added protocol around the user-code call site instead of moving the call site out.
PR #400 added rules R1 through R4. PR #412 added per-edge ownership records, a phase, an invoked prefix
and a deferred handoff. Each layer is individually correct and each makes the next defect harder to see.

This design moves the call site out for jobs 2 and 3, and leaves job 1 as the public primitive.

## 2. Evidence

Everything in this section was measured on `master` during design, not argued from the code.

**The subtree descent runs through the handler.** A three-level graph `M1 -> M2 -> M3` attached to a
context:

```
WithLifecycle()            SubjectAttached = [M2, M1]        M3 never attaches
WithContextInheritance()   SubjectAttached = [M3, M2, M1]
```

`FindSubjectsInProperties` collects one level and `AttachToProperty` never calls it, so without
`ContextInheritanceHandler` re-entering `AddFallbackContext` nothing below the first level is
discovered. `RecursiveAttachTests` states this in its header comment; no issue and no design doc records
it.

**The order a handler observes depends on its resolved service position, not its registration
position.** `ReduceFrame` runs `ServiceOrderResolver.OrderByDependencies`, which is Kahn's algorithm with
a lowest-registration-index tie-break. `ParentTrackingHandler` carries
`[RunsBefore(typeof(ContextInheritanceHandler))]`, so it and, through the tie-break, `SubjectRegistry`
are pulled ahead of the inheritance handler and observe the subtree top-down, while anything registered
without ordering attributes lands after it and observes bottom-up:

```
handler resolved BEFORE inheritance:  M2, M3, M1     (top-down)
handler resolved AFTER  inheritance:  M3, M2, M1     (bottom-up)
```

**The two notification channels disagree with each other.** Detaching the same graph:

```
EVENT.detaching(M2)
EVENT.detaching(M3)
handler.det(M3)
handler.det(M2)
```

**Detach has two overlapping recursion mechanisms.** `DetachFromProperty` collects children at
`LifecycleInterceptor.cs:215-240` and recurses at `260-268`, but `InvokeRemovedLifecycleHandlers` runs
first at line 258 and the handler re-enters through `RemoveFallbackContext`, detaching the subtree there.
The explicit recursion then finds the child already gone and no-ops.

**The root's own attach fires last**, even without inheritance, because `AttachSubjectToContext` attaches
direct children before the subject itself.

**A subject can be attached to two graphs, and the result half works.** Two contexts, each with full
tracking and its own registry, one shared subject placed under both roots:

```
registryA knows shared: True          registryB knows shared: True
registryA count=2                     registryB count=2
shared.TryGetRegisteredSubject() resolves from A, not B
parents recorded: 2
write shared.LastName -> observerA saw [LastName, FullName, FullNameWithPrefix]
                         observerB saw []
```

Both registries index the subject and parent tracking records both parents, because those write to the
subject's own data. Everything that resolves through `shared.Context` reaches graph A only, so graph B
holds a subject it can enumerate and never hears from. After leaving A the subject still resolves
through A while attached only to B.

**Nothing in the repository does a parent-to-parent multi-graph attach.** The exact detection condition
was compiled into `ContextInheritanceHandler` and the full non-integration suite run against it: 26
assemblies, no hits, no failures.

**The external consumer audited (`jf/modules/variables2`) needs one line changed.** One
`AddFallbackContext` call, on the root, used as the attach trigger. Zero `RemoveFallbackContext` calls,
zero `ILifecycleInterceptor` implementations, zero ordering attributes of its own, and no handler that
depends on the root attaching last or on children-first detach. It does have a hard requirement that a
child subject's own context resolves its parent's services, because `CommunicationSource`'s constructor
does `subject.Context.TryGetLifecycleInterceptor() ?? throw`.

## 3. The model

A subject belongs to **at most one lifecycle graph**. Within that graph it may be referenced from any
number of parents.

This is the Entity Framework model: an entity instance is tracked by exactly one change tracker, may be
referenced from many navigations inside it, and must be detached before it is handed to another context.
EF6 stated it in its exception text; EF Core keeps the rule.

Adopting it removes a concept rather than adding one. Today `ReferenceCount` is global (it lives on the
subject's data) while `IsContextAttach` and `IsContextDetach` are per-graph (they derive from one
interceptor's `_attachedSubjects`). Under one graph per subject those describe the same graph and can
never disagree, so the mismatch that produced #207 becomes unrepresentable instead of documented.

Genuine multi-graph support is rejected, not deferred, and section 11 records why.

## 4. Architecture

### Ownership after the change

| Concern | Today | After |
|---|---|---|
| Explicit service composition | `AddFallbackContext`, which also attaches | same method, pure DI, no callbacks |
| A child's inherited context | a fallback edge added by a handler through public API | internal parent link on `ContextState` |
| Root attach and detach | a side effect of `AddFallbackContext` | `AttachToContext` / `DetachFromContext` |
| Subtree descent | a side effect of that handler's topology mutation | the handler calls `ILifecycleInterceptor.AttachSubjectToContext` |
| Edge ownership protocol | PR #412's records, phases and handoff | deleted, nothing replaces it |

### The parent link

`ContextState` gains one field:

```csharp
internal readonly InterceptorSubjectContext? Parent;
```

Resolution visits own `Services`, then `FallbackContexts` in registration order, then `Parent`. The
attach edge is an ordinary entry in `FallbackContexts`; what distinguishes it is that the subject records
which context it was attached through, so the lifecycle system can release exactly that entry and reject
an attempt to remove it by hand. Explicit composition beats inheritance and the parent comes last.
`DelegationTarget` derivation extends to the parent-only case, which becomes the dominant topology: a
generated subject's executor has no services, no explicit fallbacks and one parent.

It is written through two internal methods:

```csharp
internal bool TrySetParentContext(InterceptorSubjectContext parent);
internal bool TryClearParentContext();
```

They use the same `_mutationLock`, the same single interlocked publish, and the same R4 discipline of
registering into `_usedByContexts` before publishing and unregistering after. R1 through R4 from #400 are
unchanged. Core already grants `InternalsVisibleTo` to `Namotion.Interceptor.Tracking`, so none of this
is public.

`ILifecycleInterceptor` is already in core with exactly the two methods the design needs, and
`InterceptorExecutor` already resolves it, so the new extension methods introduce no new dependency.

### Two properties this buys by construction

**Inheritance can no longer produce a pure delegation cycle.** The weaker claim is deliberate: parent
links are not a forest. A cycle is reachable when a root's first property reference comes from its own
descendant, which is an ordinary supported back-reference (#69):

```csharp
var a = new Person(context);   // attach edge -> context, count 0
a.Child = b;                   // b count 1 -> b.Parent = a.Context
b.Child = a;                   // a count 0 -> 1 -> a.Parent = b.Context
```

`a.Parent` and `b.Parent` now point at each other. What cannot happen is the failure mode that matters.
Reaching that shape requires one participant to be a root, and a root necessarily carries an attach edge
as well as its parent link, so it has two outgoing edges and is not a pure delegator. The pure delegation
cycle exception is therefore unreachable through inheritance, and the service walk's visited set handles
the rest, exactly as it does today for a cycle containing a context that owns services.

This is weaker than saying it fixes #410 symptom 1, and deliberately so. A comment on that issue argues
symptom 1 may not be organically reachable at all: a pure delegation cycle needs every context on the
loop to have no services and exactly one fallback, but a stranded edge means the earlier fallback
survives and a later re-attach adds a second, so two fallbacks mean no delegation collapse and no cycle.
Stranding pushes away from symptom 1 rather than toward it. That reasoning is derived from reading rather
than from a test, so the first deliverable for #410 is the reproduction attempt, not the fix. If the
shape cannot be built, symptom 1 is struck rather than closed.

The cycle machinery stays, since a consumer can still build a cycle deliberately with
`AddFallbackContext`, but it stops being exercised by ordinary graph construction. #402's "a subject on a
cyclic chain cannot be detached" stops mattering either way, because detach no longer resolves anything
through the chain.

**The unwritten call-site invariant becomes unbreakable.** Today the library is protected only by the
accident that every direct `AddFallbackContext` call happens on a subject no other thread can reach yet.
Nothing enforces, documents or tests that. Afterwards no consumer call can touch the inherited edge at
all.

### The owner

Each subject records the `ILifecycleInterceptor` that owns it, alongside the reference count already in
its data. Set on the first attach of any kind, root or property. Cleared on the last detach. Checked at
the top of every attach with a reference comparison, before any mutation.

Inferring ownership from the topology is not sufficient. The condition
`IsContextAttach && ReferenceCount > 1` catches a second graph claiming a subject that already has a
parent, but misses `new Person(contextA)` followed by `rootB.Children = [person]`, where the subject is a
root in A with reference count 0 so B's attach looks like an ordinary first attach.

## 5. Sequences

### Root attach

```csharp
public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
{
    subject.Context.AddFallbackContext(context);
    foreach (var interceptor in context.GetServices<ILifecycleInterceptor>())
        interceptor.AttachSubjectToContext(subject);
}

public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
{
    try
    {
        foreach (var interceptor in context.GetServices<ILifecycleInterceptor>())
            interceptor.DetachSubjectFromContext(subject);
    }
    finally
    {
        subject.Context.RemoveFallbackContext(context);
    }
}
```

The context's mutation lock is released before any interceptor runs. That is everything PR #412 was
protecting, achieved by writing two statements in order.

The detach ordering is forced rather than chosen: detach callbacks resolve their handlers through
`subject.Context`, which is the executor, and a generated subject's executor owns no services, so the
edge must still be present. PR #402 originally proposed the opposite and it loses every detach event.

Both methods live in core so the generated constructor can emit a call to them.

### Child attach, inside `LifecycleInterceptor.AttachToProperty`

```
1. claim ownership, or throw if another graph owns it            // before any mutation
2. _attachedSubjects[subject].Add(property); count = IncrementReferenceCount()
3. if (count == 1) TrySetParentContext(parentContext)            // structural, owned
4. InvokeAddedLifecycleHandlers(subject, parentContext, change)  // position unchanged
       ContextInheritanceHandler -> interceptor.AttachSubjectToContext(subject) -> next level
5. if (isFirstAttach) SubjectAttached; AttachSubjectProperty per property
```

Step 3 is the whole change. It replaces the handler's `AddFallbackContext` call, which performs the same
topology mutation from inside step 4, through public API, wrapped in an ownership protocol. Steps 4 and 5
keep their exact positions, so every ordering measured in section 2 is preserved.

The gate drops `IsContextAttach` and keeps `count == 1`. Case analysis:

| Case | count | IsContextAttach | Today | After |
|---|---|---|---|---|
| Fresh subject, first parent | 1 | true | fires | fires |
| Same subject, second parent | 2 | false | no | no |
| Detached to 0, re-attached | 1 | true | fires | fires |
| Same subject twice in one collection | early return | | no | no |
| Constructor-attached root, then placed under a parent | 1 | false | no | **fires** |

Only the last row changes, and it is exactly what #207 describes: the generated constructor puts the
subject in `_attachedSubjects`, so the later property attach is not a first attach and inheritance never
fires.

`IsContextAttach` is unchanged everywhere else. It still gates `SubjectAttached` and the per-property
attach loop, and it is still carried on `SubjectLifecycleChange`.

### Detach, inside `DetachFromProperty`

```
1. set.Remove(property); count = DecrementReferenceCount()
2. if (count == 0) TryClearParentContext(); release ownership; release the attach edge
   else if (the departing reference is the one the link points at) repoint to a survivor
3. SubjectDetaching (if last)                                    // position unchanged
4. InvokeRemovedLifecycleHandlers                                // position unchanged
       ContextInheritanceHandler -> DetachSubjectFromContext -> next level
5. the existing explicit child recursion at :260-268 stays
```

Repointing is possible only within one graph, because the detaching interceptor sees surviving references
in its own `_attachedSubjects` and has no visibility into another graph's. Under the one-graph model
that is the only case that exists.

### The reconciliation ledger, and #210

`_lastProcessedValues` is cleaned up in two places today, both inside detach loops, and the rule that
keeps it correct is written nowhere. #210 reports that an entry orphans when a property is removed from a
still-attached subject. That is not reachable: `IInterceptorSubject.Properties` is an
`IReadOnlyDictionary` whose only mutator is `AddProperties`, and neither `RegisteredSubject`,
`RegisteredSubjectProperty` nor `Namotion.Interceptor.Dynamic` offers a removal counterpart. The issue
appears to have been written from reading the code rather than from a repro.

Rather than leave a trap for whoever adds a removal API, the ledger's lifetime is bound to a notification
that already exists and that such an API would have to fire anyway, since every other
`IPropertyLifecycleHandler` depends on it:

```csharp
void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
{
    if (!change.Property.Metadata.Type.CanContainSubjects()) return;
    lock (_attachedSubjects) _lastProcessedValues.Remove(change.Property);
}
```

The two explicit `Remove` calls in `DetachFromContext` and `DetachFromProperty` then delete. The undo in
`WriteProperty`'s parent-dead check stays, because that is a rollback rather than a property leaving.

Two consequences, both recorded rather than absorbed. `LifecycleInterceptor` joins
`GetServices<IPropertyLifecycleHandler>()`, which is behaviour change 8 and needs no-op `AttachProperty`
and `RefreshCollectionProperty` implementations. And the lock edge from property detach to
`_attachedSubjects` becomes explicit, so a future removal API must not hold
`SubjectRegistry._knownSubjects` across `DetachSubjectProperty`, which is the documented order already.

The stronger fix, storing the value on the property's own data so removal is structural, is rejected: it
sits on the hot write path, and per-property data storage is exactly what #222 is trying to eliminate.

## 6. Public API and migration

| Change | Kind |
|---|---|
| `AddFallbackContext` / `RemoveFallbackContext` keep signatures, lose the attach side effect | behavioural |
| `IInterceptorSubject.AttachToContext` / `DetachFromContext` extensions in core | additive |
| `IInterceptorSubjectContext.AddTemporaryFallbackContext` returning `FallbackContextScope` | additive |
| `InterceptorExecutor` loses both overrides | snapshot change, no source break |
| `ContextInheritanceHandler` and `WithContextInheritance()` keep their names, body changes | none |

The parent link, its setters and the owner field are all internal.

### Call sites

113 in the repository. Roughly 85 need no change: `ContextDelegationCycleTests` (46),
`ContextConcurrencyTests` (22), `ContextDeepGraphTests` (8) and similar build plain context-to-context
graphs with no subject and no lifecycle, which is exactly the primitive that survives. 28 are
subject-facing across 16 files.

| Site | Becomes |
|---|---|
| `SubjectCodeGenerator.cs:246` | `AttachToContext` |
| `DynamicSubject.cs:15` | `AttachToContext` |
| `RootManager.cs:85` | `AttachToContext` |
| `ContextInheritanceHandler.cs:21,25` | descent trigger |
| `OpcUaSubjectLoader.cs:280` | `AddTemporaryFallbackContext` |
| `SubjectUpdateApplier.cs:145` | `AddTemporaryFallbackContext` |
| `SubjectItemsUpdateApplier.cs:229` | `AddTemporaryFallbackContext` |

Plus `DynamicSubjectBenchmark.cs` (2), the affected tests, and the external consumer's single line.

## 7. Edge ownership, temporary contexts, and errors

### Three kinds of edge

| Kind | Created by | Released by | Owner |
|---|---|---|---|
| Attach edge | `AttachToContext` | `DetachFromContext`, or the last detach | lifecycle |
| Parent link | `LifecycleInterceptor` at `count == 1` | last detach, or repoint | lifecycle |
| Explicit fallback | `AddFallbackContext` | the caller, never the library | consumer |

Today all three are one thing, which is why no removal decision can be stated without a condition.

The attach edge and explicit fallbacks share the `FallbackContexts` array; they are told apart by the
attach context the subject records, not by a tag on the array. A subject records one attach context, and
a second `AttachToContext` naming a different lifecycle graph is rejected by the owner check. A second
one naming a context with no `ILifecycleInterceptor` claims no ownership and is an ordinary fallback.

### This closes #207 completely

```
var child = new Person(rootContext);   // attach edge -> rootContext, owner claimed
parent.Children = [child];             // count 1 -> parent link -> parent.Context
parent.Children = [];                  // count 0 -> clear link, release ownership,
                                       //            and release the attach edge
```

`rootContext._usedByContexts` no longer retains the dead child executor, which is the 8,558-entry growth
and roughly 15 MB per cycle the issue measured. The released edge is precisely the one `AttachToContext`
created, so this is not the library deleting a consumer's registration. A subject that is only ever a
root never reaches a property detach, so only an explicit `DetachFromContext` releases its edge.

The issue's first comment records a **second reproduction with no constructor mismatch at all**, and it
has to be closed separately because the two paths diverge before they converge:

```
p1.Children = [child];   // count 1, registers P1.Context
p2.Children = [child];   // count 2, registers nothing
p1.Children = [];        // count 1, removes nothing
p2.Children = [];        // count 0, removes P2.Context, which the child never had
```

The add fires on the first attach and names P1, the remove fires on the last detach and names P2, so
P1's edge survives and retains the detached child. Change 4 closes it, because the link is cleared at
reference count zero regardless of which property the change happens to name. Change 5 additionally
removes the interim staleness at step three, where the child would otherwise still resolve through a
parent it has left. Both paths get their own reproduction test.

### Temporary fallback contexts

The three connector sites give a subject services before it enters the graph, so its property writes
carry timestamps, source origin and change notifications during population. That need is real and after
the split it is the only thing plain `AddFallbackContext` does. But the edge must not outlive the
population, or every removed connector item leaks in the same way #207 describes.

```csharp
var newItem = context.SubjectFactory.CreateSubject(property);
using (newItem.Context.AddTemporaryFallbackContext(parent.Context))
{
    ApplyPropertyUpdates(newItem, itemProperties, context);
    context.SetPropertyValue(property, propertyUpdate.Timestamp, newItem);
}
```

The parent link takes over at the assignment inside the block, the temporary edge drops at the closing
brace, and a throw during population leaves an unattached subject with no edges. The OPC UA loader's
conditional case uses a scope struct that no-ops when default:

```csharp
using var scope = isNewSubject
    ? subjectToLoad.Context.AddTemporaryFallbackContext(subject.Context)
    : default;
```

Naming this separately is also what makes the migration loud. Adding a lifecycle-bearing context to a
not-yet-attached subject is the same shape whether the caller meant to attach a root or to seed a child,
so no rule can separate them while both go through one method.

### What throws

| Condition | Result |
|---|---|
| Attaching a subject owned by another graph | `InvalidOperationException`, before any mutation |
| `RemoveFallbackContext` targeting a subject's attach edge | `InvalidOperationException` naming `DetachFromContext` |
| `AddFallbackContext` adding a lifecycle-bearing context to an unattached subject | `InvalidOperationException` naming `AttachToContext` and `AddTemporaryFallbackContext` |
| Delegation cycle on resolution | unchanged |

`AttachToContext` called twice with the same context stays idempotent: the edge add returns false and the
interceptor finds the subject already in `_attachedSubjects`.

Handler exceptions are unchanged. A throwing `ILifecycleHandler` still propagates and still leaves
partial bookkeeping, exactly as on `master`. That is #384 and it stays out.

## 8. Behaviour changes

The complete list. Anything discovered beyond these eight is escalated, not absorbed.

1. `AddFallbackContext` stops attaching.
2. `RemoveFallbackContext` stops detaching a root, and throws if aimed at the attach edge.
3. #207 closed, including the measured leak, through the owned attach edge.
4. #410 symptom 1: the parent link clears unconditionally at reference count zero, rather than only when
   property values happen to have been cleared.
5. #410 symptom 2: the parent link repoints when its target leaves and other references survive.
6. Attaching a subject owned by another graph throws instead of half-attaching silently.
7. A throwing detach interceptor no longer prevents the attach edge from being removed.
8. `LifecycleInterceptor` appears in `GetServices<IPropertyLifecycleHandler>()`, one extra entry in that
   list. The relative order of the existing entries is unchanged, since adding a node to the resolver's
   input cannot reorder the others under its lowest-registration-index tie-break.

Ordering is deliberately **not** in this list. Fixing the traversal order to top-down was considered and
rejected: the audit shows it would break nothing in the codebases we can see and would strictly improve
their ancestor-dependent reads, but the handler-preserving design keeps every order bit-identical for
free, so there is no reason to spend the risk here. It stays available as its own change with its own
evidence.

## 9. Verification

### Two categories with opposite gates

Every test added by this work belongs to exactly one of these, and the gate is what makes it worth
writing:

- **Characterization tests must pass on unmodified `master`.** They lock down behaviour we intend to
  preserve. If one fails on `master`, the test is wrong, not the code.
- **Reproduction tests must fail on unmodified `master`.** One per closed issue, written from the issue's
  own repro. If one passes on `master`, either the issue is already fixed or we have misunderstood it,
  and both are worth knowing before any production code is written.

The second gate is the stronger one. It is what stops us declaring an issue closed on the strength of a
test written after the fix, which necessarily agrees with the fix.

This is also why the first commit of the pull request adds tests only. At that commit the production code
is still `master`'s, so both gates are verifiable by checking it out and running the suite. Folded into a
mixed commit, neither claim can be checked again.

### Characterization tests

These land first and must pass on unmodified `master`. None of this is pinned today.

1. Attach and detach event sequences for a three-level graph, capturing both channels.
2. The resolved-position dependency, including that `ParentTrackingHandler`'s `[RunsBefore]` pulls
   `SubjectRegistry` to position 0 through the tie-break.
3. The root's own attach fires last.
4. Grandchildren do not attach under `WithLifecycle()` alone.
5. A child's own context resolves the parent's services, and so does a grandchild's, via
   `TryGetService<ISubjectRegistry>()` and `TryGetLifecycleInterceptor()`. Depth is asserted explicitly
   because it is where a parent link differs most from a fallback edge. This is the one requirement the
   audited external consumer depends on and nothing currently tests it: `CommunicationSource`'s
   constructor does `subject.Context.TryGetLifecycleInterceptor() ?? throw` on a child subject.
6. Multi-parent attach-once and detach-once counts.
7. `IPropertyLifecycleHandler` invocation order on property attach and detach, which the #210 fix adds an
   entry to.

### Each change to its evidence

| # | Pinned by |
|---|---|
| 1 | root attach through `AttachToContext` only |
| 2 | rewritten `SubjectDetaching_FiresForRootSubject_WhenContextRemoved` |
| 3 | both of the issue's repros, the constructor-mismatch path and the two-parent path, each with a weak-reference probe on `_usedByContexts` |
| 4 | detach that leaves property values set |
| 5 | two parents, remove the first, assert resolution follows the survivor |
| 6 | both shapes: parent-to-parent, and root-in-A-then-child-in-B |
| 7 | throwing detach interceptor, assert the edge is gone |

### Oracles that must not move

The four `.verified.txt` snapshots in `Namotion.Interceptor.Tracking.Tests` are the ordering gate. Any
movement is a signal to stop, not a snapshot to accept. Both `PublicApi.verified.txt` files change only
by the `InterceptorExecutor` overrides disappearing and the new extensions appearing.

`ContextConcurrencyFuzzTests` needs its model extended with parent links, since they are a third mutable
edge kind under the same lock and the same R4 discipline.

### New concurrency coverage

Parent link set and clear reuse `AddFallbackContext`'s publish and `_usedByContexts` protocol exactly, so
the fuzz model extension covers them. The owner claim is serialized by `lock (_attachedSubjects)` within
a graph but not across graphs, so it gets a directed test: two threads attaching the same subject to two
graphs, exactly one wins, the loser throws, no partial state on either side. Repointing gets a concurrent
double detach.

### Mutants that must die

Following the practice established in #400 and #412: restore `IsContextAttach` to the link gate; make
the link clear conditional on property values; delete the owner check; delete the `finally`; skip the
repoint. Each must fail its corresponding test.

## 10. Staging

One pull request on `design/context-inheritance-parent-link`, built from commits in this order. The
ordering is not cosmetic: each commit is a point at which the suite should be green, and the first two
carry gates that stop meaning anything once they are folded into later work.

| Commit | Content | Gate |
|---|---|---|
| 1 | Characterization tests only | must be green with `master`'s production code |
| 2 | Reproduction tests: #207 both paths, #410 symptom 2, #402 defect 1, the two-graph shapes | must be **red**, each for the reason its issue states |
| 3 | `AttachToContext` / `DetachFromContext`, migrate root call sites | behaviour-neutral, since `AttachSubjectToContext` is idempotent while `AddFallbackContext` still attaches |
| 4 | `ContextState.Parent`, internal setters, `LifecycleInterceptor` sets and clears it, handler body becomes the descent trigger | snapshots must not move; #410 turns green |
| 5 | Remove the executor overrides, add the loud errors, add `AddTemporaryFallbackContext`, migrate the three connector sites | the breaking one |
| 6 | Owner field and multi-graph exception, attach-edge release, repoint, `finally` | #207 and #402 turn green |
| 7 | The #210 fix: ledger cleanup bound to `IPropertyLifecycleHandler.DetachProperty` | characterization test 7 records the new handler entry |
| 8 | Consumer and design docs | see below |

Commits 4 and 5 change allocations per attach. The prediction is one fewer allocation and roughly 24
fewer bytes per attached subject, because a one-element `ImmutableArray` is replaced by a field, so the
branch carries a `RegistryBenchmark` run on `AddLotsOfPreviousCars` and `ChangeAllTires` before it is
proposed for merge.

If review stalls on the whole, the natural cut line is between commits 4 and 5: everything up to and
including 4 is behaviour-neutral for existing callers.

### Documentation

Consumer-facing, `docs/interceptor.md` and `docs/tracking.md`:

- the three kinds of edge and who owns each
- `AttachToContext` / `DetachFromContext` as the way a root joins and leaves a graph, replacing
  `AddFallbackContext` for that purpose
- `AddTemporaryFallbackContext` and the pattern it exists for
- one graph per subject, and what the exception means when it fires

Design-facing, `docs/design/tracking-lifecycle.md`:

- the parent link, its ownership, and the gate that sets it
- that `_lastProcessedValues` entries live exactly as long as their property is attached, and that any
  future property removal API must route through `DetachSubjectProperty` without holding
  `SubjectRegistry._knownSubjects`
- the resolved-position ordering dependency from section 2, since this design deliberately preserves it
  and no issue records it
- the global versus per-graph reference count distinction, and why one graph per subject collapses it

### PR #412

Closed unmerged rather than reverted, since it never landed on `master`. Two things carry forward: the
`TryPublishFallbackContext` / `TryUnpublishFallbackContext` extraction, which serves the surviving
primitive, and the corrected XML documentation on `IInterceptorSubjectContext`. Its tests go with the
protocol they test.

### Final step, human gated

Updating issues and merging the pull request are **not** part of the implementation plan and are not done
autonomously. The plan ends at commits and pushes to this branch. The list below is what a human then
approves and executes.

Each row below was checked against the issue's comments, not only its body, because several carry
corrections that the body does not.

| Issue | Action |
|---|---|
| #402 | Close. All five defects dissolve: no callbacks inside the edge mutation means nothing to order, claim or hand off. Its first comment concluded that "the complete fix needs a decision about where lifecycle callbacks run, not just a reordering", which is what this design decides. |
| #207 | Close, citing **both** reproductions. The body's path is a constructor context differing from the lifecycle parent. The first comment adds a second path with no constructor mismatch at all: two parents, where the add fires on the first attach and names P1 while the remove fires on the last detach and names P2, so P1's edge survives. Commit 2 carries a reproduction for each, since the comment notes they diverge before they converge. |
| #410 | Close symptom 2 and the retention it causes. **Do not claim symptom 1** without the reproduction attempt: its own comment argues the pure delegation cycle may be organically unreachable, because stranding leaves two fallbacks rather than one and two fallbacks cannot collapse. If the attempt fails, symptom 1 is struck and the issue closes narrower. |
| #210 | Close as not reachable, with the finding that no property removal API exists, and note that commit 7 makes the leak structurally impossible if one is added later. |
| #411 | Update, do not close. Both windows its comment documents, the claimed record and the deferred handoff, are artifacts of PR #412 and vanish with it. What remains is the shape that predates #412: `DetachFromContext` runs interceptors before removing the edge, so a concurrent add can be told `false` and then lose the edge. Narrowed from every child detach to an explicit concurrent root detach. The comment's OOM-stranding motivation disappears entirely with the record list. |
| #384 | Update, narrowed on two of its three recorded cases. The attach-tail case loses route 2, since the deferred handoff that created it goes with #412; route 1, a handler whose write orphans the subject being attached, is pre-existing and untouched. The detach-half case still raises on a cyclic chain, but such a chain now requires a consumer to have built one deliberately. Its stated blocker is removed, since inheritance is no longer a handler, and the remaining work is rollback on throw inside `AttachToProperty`. |
| #412 | Close unmerged, referencing this design. |

Two findings from section 2 have no issue and none is being filed: the two-graph half-support, which
change 6 closes, and the resolved-position ordering dependency, which this design preserves deliberately.
Both live in this spec and in `docs/design/tracking-lifecycle.md`, and the pull request description
records them.

## 11. Out of scope

Stated explicitly so the spec cannot be read as covering them.

- **#384**, handler exception recovery. Its stated blocker ("context inheritance must propagate to a
  subject before its children attach") does dissolve here, since inheritance stops being a handler. But
  the remaining fix is rollback on throw inside `AttachToProperty`, which is orthogonal to the parent
  link. The only coupling is textual, so sequencing the two avoids a conflict.
- **#403, #404, #406**, the `TryAddService` factory under the mutation lock and service equality. Same
  class of problem, different call site, different fix.
- **#409**, measuring the copy-on-write memory trade-offs.
- **Fixing the traversal order to top-down.** See section 8.
- **Genuine multi-graph support.** Rejected rather than deferred. It would require: graph identity
  threaded through the registry, path, connector and source APIs, because `TryGetService` throws when two
  services of a type resolve and `TryGetRegisteredSubject` resolves the registry that way; a merged
  interceptor chain whose interleaving between two graphs is undetermined and where short-circuiting
  interceptors decide for both; the return of delegation cycles, since building graph 1 with A above Q
  and graph 2 with Q above A makes each the other's parent; ownership tags on every link; and a permanent
  per-graph versus total reference count distinction. If it is ever wanted it should be a deliberate
  design with graph identity as a first-class parameter, and the exception added here is what makes that
  discoverable instead of silent.
- **A subject inheriting from a parent it left, across graphs.** Under the one-graph model this cannot
  arise.

## 12. Rejected: running the external consumer's suite as a gate

Building the library locally and running the audited consumer's test suite against it, through a
`ProjectReference` swap or a local feed, was considered as a merge gate and rejected. It would add a
build dependency on a repository this one does not control, for coverage of behaviours nobody has written
down.

The one requirement it was protecting is specific and testable here: a child subject's own context must
resolve services registered further up, because `CommunicationSource`'s constructor does
`subject.Context.TryGetLifecycleInterceptor() ?? throw`. Characterization test 5 asserts exactly that, at
child and grandchild depth. What is given up is breadth over unenumerated behaviour, which is a real but
unbounded loss and not worth the coupling.
