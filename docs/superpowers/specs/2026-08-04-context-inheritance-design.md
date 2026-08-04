# Context inheritance as an owned parent link

Design document. Written 2026-08-04 against `master` at `e616c769`. Revised after two independent
adversarial reviews, which are cited inline where they changed a decision.

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

Nine open issues live in this area. Six are the same shape: the library runs user-supplied code in the
middle of a multi-step state transition. `TryAddService` runs a factory under the mutation lock (#403,
#404, #406), `LifecycleInterceptor` runs handlers mid-reconciliation (#384), and `InterceptorExecutor`
runs lifecycle interceptors between publishing an edge and owning it (#402, with #411 as the window
those callbacks force open). The remaining two, #207 and #410, are the derived-graph problem: the object
graph and the context graph are kept in step imperatively by a handler reacting to lifecycle events.

Every fix so far has added protocol around the user-code call site instead of moving the call site out.
PR #400 added rules R1 through R4. PR #412 added per-edge ownership records, a phase, an invoked prefix
and a deferred handoff. Each layer is individually correct and each makes the next defect harder to see.

This design moves the call site out for jobs 2 and 3, and leaves job 1 as the public primitive.

## 2. Evidence

Everything here was measured on `master`, and every item was independently re-measured during review.

**The subtree descent runs through the handler.** A three-level graph `M1 -> M2 -> M3`:

```
WithLifecycle()            SubjectAttached = [M2, M1]        M3 never attaches
WithContextInheritance()   SubjectAttached = [M3, M2, M1]
```

`FindSubjectsInProperties` collects one level (`LifecycleInterceptor.cs:407-436`) and `AttachToProperty`
never calls it, so without `ContextInheritanceHandler` re-entering `AddFallbackContext` nothing below the
first level is discovered. `RecursiveAttachTests.cs:6-14` states it in a header comment; no issue and no
design doc records it.

**The order a handler observes depends on its resolved service position, not its registration position.**
`ReduceFrame` runs `ServiceOrderResolver.OrderByDependencies`, Kahn's algorithm with a lowest-index
tie-break (`ServiceOrderResolver.cs:158-181`), behind a `[RunsFirst]`/`[RunsLast]` partitioning fast path
(`:42-95`). `ParentTrackingHandler` carries `[RunsBefore(typeof(ContextInheritanceHandler))]`, so it and,
through the tie-break, `SubjectRegistry` are pulled ahead of the inheritance handler:

```
handler resolved BEFORE inheritance:  M2, M3, M1     (top-down)
handler resolved AFTER  inheritance:  M3, M2, M1     (bottom-up)
```

**Handlers ahead of the inheritance handler see a child that resolves nothing.** Measured with a probe
carrying `[RunsBefore(typeof(ContextInheritanceHandler))]` on a fully configured context:

```
order: SubjectRegistry, ParentTrackingHandler, ProbeHandler, ContextInheritanceHandler
probe(Child)      rc=1 resolvesRegistry=0 resolvesLifecycle=0
probe(Grandchild) rc=1 resolvesRegistry=0 resolvesLifecycle=0
```

The edge does not exist until the inheritance handler adds it, so every handler ahead of it sees an
unresolvable subject. This design changes that, and it is behaviour change 11.

**The two notification channels disagree.** Detaching the same graph:

```
EVENT.detaching(M2)
EVENT.detaching(M3)
handler.det(M3)
handler.det(M2)
```

**Detach has two overlapping recursion mechanisms.** `DetachFromProperty` collects children at
`LifecycleInterceptor.cs:215-240` and recurses at `:260-268`, but `InvokeRemovedLifecycleHandlers` runs
first at `:258` and the handler re-enters through `RemoveFallbackContext`, detaching the subtree there.
The explicit recursion then finds the child already gone and no-ops via `:207-210`.

**The root's own attach fires last**, even without inheritance (`LifecycleInterceptor.cs:42-50`).

**The three connector sites depend on the attach, not on service resolution.** This was found in review
and it invalidated the original migration plan. Each site adds the edge and then immediately calls code
that requires registry membership, which only a lifecycle attach provides:

| Site | Immediately followed by | If not attached |
|---|---|---|
| `OpcUaSubjectLoader.cs:280` | `LoadSubjectAsync`, whose first act is `TryGetRegisteredSubject() ?? return` (`:61-65`) | the entire subtree below every new node is silently never browsed |
| `SubjectUpdateApplier.cs:145` | `ApplyPropertyUpdates` → `TryGetRegisteredProperty` → `if (null) return` (`:77-79`) | every property update on the new item is silently dropped |
| `SubjectItemsUpdateApplier.cs:229` | the same, via `SubjectUpdateApplier` at `:231` | the same |

All three are covered only by integration-tagged tests, which `AGENTS.md`'s default command excludes, so
this would have shipped green.

**A subject can be attached to two graphs, and the result half works.**

```
registryA knows shared: True          registryB knows shared: True
registryA count=2                     registryB count=2
shared.TryGetRegisteredSubject() resolves from A, not B
parents recorded: 2
write shared.LastName -> observerA saw [LastName, FullName, FullNameWithPrefix]
                         observerB saw []
```

Both registries index the subject and parent tracking records both parents, because those write to the
subject's own data. Everything resolving through `shared.Context` reaches graph A only, so graph B holds
a subject it can enumerate and never hears from.

**Nothing in the repository does a parent-to-parent multi-graph attach.** The detection condition was
compiled into `ContextInheritanceHandler` and the full non-integration suite run against it: no hits, no
failures. It does not cover the root-in-A-then-child-in-B shape, which is why ownership is explicit.

**The external consumer audited (`jf/modules/variables2`) needs one line changed.** One
`AddFallbackContext`, on the root. Zero `RemoveFallbackContext`, zero `ILifecycleInterceptor`
implementations, zero ordering attributes of its own. It does require that a child subject's own context
resolves its parent's services, because `CommunicationSource`'s constructor does
`subject.Context.TryGetLifecycleInterceptor() ?? throw`.

## 3. The model

A subject belongs to **at most one lifecycle graph**. Within that graph it may be referenced from any
number of parents.

This is the Entity Framework model: an entity instance is tracked by exactly one change tracker, may be
referenced from many navigations inside it, and must be detached before it is handed to another context.

Adopting it removes a concept rather than adding one. Today `ReferenceCount` is global (it lives on the
subject's data) while `IsContextAttach` and `IsContextDetach` are per-graph (they derive from one
interceptor's `_attachedSubjects`). Under one graph per subject those describe the same graph and can
never disagree, so the mismatch that produced #207 becomes unrepresentable.

Genuine multi-graph support is rejected, not deferred. Section 12 records why.

## 4. Architecture

### Ownership after the change

| Concern | Today | After |
|---|---|---|
| Explicit service composition | `AddFallbackContext`, which also attaches | same method, pure DI, no callbacks |
| A child's inherited context | a fallback edge added by a handler through public API | internal parent link on `ContextState` |
| Root attach and detach | a side effect of `AddFallbackContext` | `AttachToContext` / `DetachFromContext` |
| Subtree descent | a side effect of that handler's topology mutation | the handler calls `ILifecycleInterceptor.AttachSubjectToContext` |
| Edge ownership protocol | PR #412's records, phases and handoff | relocated to two per-subject records, and reduced |

That last row is deliberately not "deleted". Against `master` the only code this removes is the two
`InterceptorExecutor` overrides. What it removes from PR #412's branch is replaced by a smaller
per-subject version of the same idea: an owner and an attach-context record. The honest case for the
change is that `AddFallbackContext` gets one meaning, no user code runs inside an edge mutation, and the
inherited edge becomes untouchable from consumer code.

### The parent link

`ContextState` gains one field:

```csharp
internal readonly InterceptorSubjectContext? Parent;
```

Resolution visits own `Services`, then `FallbackContexts` in registration order, then `Parent`. The
attach edge is an ordinary entry in `FallbackContexts`; what distinguishes it is that the executor
records which context it was attached through. Explicit composition beats inheritance and the parent
comes last. `DelegationTarget` derivation extends to the parent-only case, which is the dominant
topology.

Written through two internal methods on `InterceptorSubjectContext`:

```csharp
internal bool TrySetParentContext(InterceptorSubjectContext parent);
internal bool TryClearParentContext();
```

Same `_mutationLock`, same single interlocked publish, same R4 discipline. R1 through R4 from #400 are
unchanged. Core already grants `InternalsVisibleTo` to `Namotion.Interceptor.Tracking`
(`Namotion.Interceptor.csproj:17`), so none of this is public.

`ContextState.IsEmpty` must be extended to account for `Parent`. Today a parent-only state is only
non-empty by virtue of having a `DelegationTarget`, and that coupling is invisible.

### The cycle argument, corrected

The first version of this design claimed parent links can never produce a pure delegation cycle. Both
reviewers refuted it, with different counterexamples, and both counterexamples ran through the
repoint-on-partial-detach rule. **That rule is removed** (see section 5), and with it the counterexamples.

What remains true is narrower. The link is set only at `count == 1`, so a link cycle needs two subjects
each of which acquired its first property reference from the other. Reaching that requires one of them to
be a root, and a root carries an attach edge as well as its link, so it has two outgoing edges and is not
a pure delegator. The pure delegation cycle exception is therefore unreachable through inheritance, and
the service walk's visited set handles the rest, as it already does for cycles containing a context that
owns services.

Two guards are load-bearing for that argument and are stated as rules rather than left implicit:

- **The link is never set to the subject's own context.** `a.Mother = a` is legal and reaches
  `AttachToProperty` with the parent being the subject itself, which would otherwise self-delegate and
  make every access on that subject throw. Verified reachable during review.
- **No link is set outside the `count == 1` gate.** This is what the removed repoint violated.

This design does not claim to fix #410 symptom 1. That issue's own comment argues it may be organically
unreachable on `master`, because stranding leaves two fallbacks and two fallbacks cannot collapse. The
reproduction attempt is a deliverable; if the shape cannot be built, symptom 1 is struck.

### The owner

Two records are needed: which lifecycle graph owns the subject, and which context it was attached
through. **Both live on `InterceptorExecutor` as plain reference fields**, not in `subject.Data` and not
on the base `InterceptorSubjectContext`.

```csharp
// on InterceptorExecutor, which is the only context that has a subject
private ILifecycleInterceptor? _owner;
private IInterceptorSubjectContext? _attachContext;   // null, or the context attached through
```

`subject.Data` was the first choice and is worse on every axis. It is a `ConcurrentDictionary`, so each
record costs a node allocation of roughly 50 to 60 bytes plus table pressure, one per attached subject,
which alone outweighs everything the parent link saves. And the guard that reads `_attachContext` lives
inside `AddFallbackContext`, a method on the context, so a subject-side record means a cross-object
lookup on a path that is otherwise a field read. On the executor they are two reference fields, 16 bytes
on an object that already exists per subject.

The base class was the second choice and is also wrong. Both guards are executor-only semantics: on a
plain `InterceptorSubjectContext.Create()` context, adding a lifecycle-bearing context is exactly how
services are composed and must not throw. Fields on the base would give every standalone context state
that can never mean anything.

The guards are `protected virtual` hooks called from inside the base's critical section, not method
overrides wrapping it. Section 5 specifies the lock discipline and the guards; this subsection only fixes
where the fields live.

Inferring ownership from topology instead of recording it is not sufficient:
`IsContextAttach && ReferenceCount > 1` misses `new Person(contextA)` followed by
`rootB.Children = [person]`, where the subject is a root in A with reference count 0, so B's attach looks
like an ordinary first attach.

One limit, recorded rather than absorbed: the owner is an `ILifecycleInterceptor` reference, so two root
contexts sharing one tracking context as a fallback count as one graph while having two registries, and
the two-graph finding in section 2 is not closed in that configuration.

## 5. Sequences

### State and synchronisation

Five reviews found defects here and none found one in the thesis. Every material finding was in a
protocol built to make root attach and detach atomic against each other. That protocol is removed. Root
attach publishes an edge and then runs lifecycle callbacks, two steps, and no state machine makes two
steps one without the kind of negotiation PR #412 built. This design does not pretend otherwise.

What remains is deliberately small.

**Two fields on `InterceptorExecutor`:**

```csharp
private IInterceptorSubjectContext? _attachContext;   // null, or the context this subject was attached through
private ILifecycleInterceptor? _owner;                // the graph that owns this subject
```

**`_attachContext` is written and read only under `_mutationLock`**, the same lock that publishes the
edge set. It has two values, not four. It exists for exactly four purposes: releasing the attach edge
when the subject leaves the graph, which is what closes #207; letting `RemoveFallbackContext` reject an
attempt to pull the attach edge by hand; making detach run exactly once; and answering
`TryGetAttachContext`.

**`_owner` is claimed and released only by `LifecycleInterceptor`, always while it holds
`_attachedSubjects`, with `_mutationLock` nested inside for the field write.** That placement is what
makes it correct rather than clever. The reference count lives under that monitor, so ownership is only
ever released where the count is already known, and the cross-graph race is still serialised, because two
graphs hold two different monitors but contend for the same executor's `_mutationLock`. An earlier
version released ownership from `AbortAttach` and `CompleteDetach` "if the subject holds no property
references", which read the count from outside the monitor that guards it. That is the same
time-of-check race this design removed elsewhere, and it is on the mutant list.

**Guards run inside the base's critical section**, as a `protected virtual` hook that
`InterceptorExecutor` overrides, not as a wrapping method override. Reading the state, releasing, and
then calling `base` is check-then-act across a lock boundary; holding the lock across `base` would drag
`InvalidateUsingContexts` inside it, and #400 deliberately runs that outside.

| Entry point | Guard |
|---|---|
| `AddFallbackContext(x)` | throws when `x` carries an `ILifecycleInterceptor` and the subject is **not lifecycle-attached**, naming `AttachToContext` |
| `RemoveFallbackContext(x)` | throws when `x` is the attach context, naming `DetachFromContext` |

"Not lifecycle-attached" means `_owner` is null. It is deliberately not "the attach state is null": a
generated constructor handed a plain `InterceptorSubjectContext.Create()` context, which is how most of
`Namotion.Interceptor.Tests` is written, has an attach context and no owner, and must keep composing
services freely.

#### What is not guaranteed

`AttachToContext` and `DetachFromContext` are two-step operations and are **not atomic against each
other**. Running them concurrently on the same subject reproduces #402 defect 1 and #411, at `master`'s
behaviour and no worse. Both are already narrowed by the thesis, from every child detach to an explicit
concurrent root operation, because `ContextInheritanceHandler` stops calling the public mutators
entirely. Section 10 records the follow-up that closes them.

#### Aliasing is allowed and made safe

Earlier versions tried to forbid two edges pointing at the same context. Review produced two-line
sequences that reach it anyway, in both directions, and `AddFallbackContext` dedups on `Contains`
(`InterceptorSubjectContext.cs:125`) so an explicit fallback and the attach edge collapse into one array
entry regardless.

So aliasing is permitted, and the reverse entry is what becomes careful: **after removing any edge
targeting `X`, the `_usedByContexts` entry is dropped only if no remaining edge on this context targets
`X`.** Registration stays unconditional and idempotent, preserving R4's superset property. Without this,
removing one of two aliased edges unregisters the sole reverse entry while the other still resolves
through it, so invalidation never reaches the subject again and its compiled chain silently keeps an
interceptor set the graph no longer has. That is #400's defect 6, the one it rates most serious, and it
is unreachable on `master` because one edge kind plus dedup means two edges to one target cannot exist.

### Root attach and detach

```csharp
// core, because the generated constructor emits a call to it
public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
{
    // Resolved before the edge is published, so a cyclic chain throws with nothing committed.
    // This is #402 defect 4: on master the executor publishes first and resolves after, so a
    // failing resolve leaves the edge registered with no attach callback ever having run.
    var interceptors = context.GetServices<ILifecycleInterceptor>();

    if (!subject.Context.TryRecordAttachContext(context))   // under _mutationLock; false if already this context
        return;

    subject.Context.AddFallbackContext(context);

    foreach (var interceptor in interceptors)
        interceptor.AttachSubjectToContext(subject);        // claims _owner, under _attachedSubjects
}

// Namotion.Interceptor.Tracking, because the reference-count guard needs the count
public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
{
    // Resolved first, so a cyclic chain throws before anything has changed.
    var interceptors = context.GetServices<ILifecycleInterceptor>();

    // A subject that is also a child must be removed from its parents first. Detaching it here would
    // remove its _attachedSubjects entry (LifecycleInterceptor.cs:172) while reading the reference
    // count rather than decrementing it (:186), stranding the count and the ownership. This is a guard
    // against a programming error, not against a race; see "What is not guaranteed" above.
    if (subject.GetReferenceCount() != 0)
        throw new InvalidOperationException(/* detach it from its parents first */);

    if (!subject.Context.TryClearAttachContext(context))    // under _mutationLock; exactly one winner
        return;

    try
    {
        foreach (var interceptor in interceptors)
            interceptor.DetachSubjectFromContext(subject);  // releases _owner, under _attachedSubjects
    }
    finally
    {
        subject.Context.RemoveAttachEdge(context);          // internal, bypasses the public guard
    }
}
```

`TryClearAttachContext` is the whole of change 7. Two concurrent `DetachFromContext` calls both take
`_mutationLock`; one finds the record naming the context, clears it and proceeds, the other finds null
and returns having called nothing. Exactly one interceptor pass, in one critical section, with no
transitional state to get wedged in. `LifecycleInterceptor`'s detach happens to be idempotent, but #402
is explicit that nothing in the `ILifecycleInterceptor` contract requires that of a consumer.

Clearing the record before the interceptors run means `RemoveFallbackContext` no longer rejects during
the window, so a consumer can pull the edge mid-detach. That is consistent with what this section
already concedes about concurrent root operations, and the follow-up in section 10 closes both together.

The cleanup uses an internal `RemoveAttachEdge` rather than the public method, because the public guard
cannot distinguish the design's own cleanup from a consumer's call and either answer is wrong.

**Failure modes:**

| Failure | Result |
|---|---|
| The attach resolve throws (cyclic chain on `context`) | nothing changed, because the resolve precedes the record and the edge |
| `TryRecordAttachContext` throws (another context already recorded) | nothing changed |
| An attach interceptor throws | the edge and the record remain, and the subject is partly attached. This is #384's rollback problem and it is out of scope; a retry finds the record already set and returns, so the missed callbacks are not re-run |
| The detach resolve throws (cyclic chain on `context`) | nothing changed, because the resolve precedes the guard and the transition |
| A detach interceptor throws | the `finally` still removes the edge (change 6), and the record is already clear, so the subject can be re-attached |
| `RemoveAttachEdge` throws | it replaces any in-flight exception. The record is already clear, so nothing is wedged |

### Child attach, inside `LifecycleInterceptor.AttachToProperty`

```
1. claim ownership under the executor's _mutationLock: null -> this.
     If another graph owns it, throw. The check and the claim are one critical section.
2. _attachedSubjects[subject].Add(property); count = IncrementReferenceCount()
3. if (count == 1
       && parentContext is not subject.Context               // self-context guard
       && parentContext is not the recorded attach context)  // the attach edge already provides it
     TrySetParentContext(parentContext)
4. InvokeAddedLifecycleHandlers(subject, parentContext, change)   // position unchanged
       ContextInheritanceHandler -> interceptor.AttachSubjectToContext(subject) -> next level
5. if (isFirstAttach) SubjectAttached; AttachSubjectProperty per property
```

**Step 1 is the check and the claim in one critical section, per subject.** An earlier version hoisted a
batch-level check into `WriteProperty` so it could claim to run "before any mutation". Review refuted
that: hoisting the *check* separates it from the *claim*, and since two graphs hold different
`_attachedSubjects` monitors, A-checks, B-checks, A-claims, B-throws is reachable, which is exactly what
the hoist existed to prevent. Taking the executor's `_mutationLock` cannot be beaten by that interleaving,
because both graphs contend for that same lock even though their monitors differ.

The honest cost is stated rather than engineered away. `WriteProperty` calls `next(ref context)` before
taking the lock (`LifecycleInterceptor.cs:294`), so the backing store already holds the new value when a
cross-graph rejection throws, and earlier items of the same batch are already attached. That is a
partially applied batch, it is #384's shape, and it is out of scope here.

**Step 3 carries two guards, and the second one is inverted from the earlier version.** The self-context
guard prevents `a.Mother = a` from self-delegating, which would make every access on that subject throw.
The second guard skips setting the link when the attach edge already names that context, which is the
case for all three connector sites, since they call `AttachToContext(parent.Context)` and then assign
into a property of that same parent.

The earlier version did the opposite: it set the link and then released the attach edge. Review took that
apart. Releasing the edge leaves the attach-context record describing an edge that no longer exists, and
there is no good answer to what it should then hold: keep it and `DetachFromContext` becomes callable on
a live child, which removes the subject from `_attachedSubjects` while its parent still references it;
clear it and `IsAttached` reports false for an attached subject while a legitimate `DetachFromContext`
silently no-ops. Worse, once the edge is gone a re-attach makes `AddFallbackContext` return `true`, so
the seed runs again and clobbers `_lastProcessedValues`, which is the hazard the guard in the root path
exists to prevent.

Not setting the link achieves the same thing with none of that: one outgoing edge, so the subject stays a
pure delegator, one record instead of two, and no state the machine in section 5 cannot express. The
motivation is real and was measured during review: on `master` a connector item ends with exactly one
fallback and a live `DelegationTarget`, because the inheritance handler's duplicate `AddFallbackContext`
returns `false`. Doing nothing here would be a regression, not a neutral choice.

One case escapes the guard, and it is harmless: the OPC UA dedup cache hit at
`OpcUaSubjectLoader.cs:263-269` assigns an already-seeded subject into a *different* parent's property,
so the contexts differ, the link is set, and that subject carries two edges. So "all three connector
sites" describes the common path, not every path through them.

Steps 3 and 4 keep their positions, so every ordering measured in section 2 is preserved. What is not
preserved is what those handlers can *see*: the link now exists before they run, where today the edge
does not. That is behaviour change 11.

The gate drops `IsContextAttach` and keeps `count == 1`:

| Case | count | IsContextAttach | Today | After |
|---|---|---|---|---|
| Fresh subject, first parent | 1 | true | fires | fires |
| Same subject, second parent | 2 | false | no | no |
| Detached to 0, re-attached | 1 | true | fires | fires |
| Same subject twice in one collection | early return at `:121-124` | | no | no |
| Constructor-attached root, then placed under a parent | 1 | false | no | **fires** |
| Root in graph A, then child in graph B | 1 | true | fires | **throws**, owner check |
| `a.Mother = a` on a constructor-attached root | 1 | false | no | **no**, self-context guard |
| Connector item, `AttachToContext(parent.Context)` then assigned under that parent | 1 | false | no | **no**, the attach edge already names that context |

### Detach, inside `DetachFromProperty`

```
1. set.Remove(property); if absent, return
2. isLastDetach = set.IsEmpty; if last, remove from _attachedSubjects, collect children,
   detach properties
3. count = DecrementReferenceCount()
4. try:
       if (isLastDetach) SubjectDetaching
       InvokeRemovedLifecycleHandlers        // descent happens here
   finally:
       if the subject is STILL absent from _attachedSubjects:
           TryClearParentContext(); release the attach edge; release ownership
5. the existing explicit child recursion at :260-268 stays
```

The `finally` re-reads `_attachedSubjects` rather than trusting the `count == 0` captured at step 3. That
is a correction from the third review, which found a hole the `finally` itself introduced: a handler
running in step 4 can re-attach the subject, since `_attachedSubjects.Remove` at
`LifecycleInterceptor.cs:218` has already run. `AttachToProperty` then sees `existed == false`, the count
goes 0 to 1, and step 3 sets a fresh link. A `finally` firing on the captured count would wipe all of it,
with no way to re-establish the link because the `count == 1` gate is spent. On `master` the equivalent
removal happens inside the handler chain (`ContextInheritanceHandler.cs:23-26`), so a later handler's
re-attach survives, and the re-read preserves that.

**The `finally` is the correction that matters most.** The first version released at step 2, before the
handlers. Both reviewers refuted it: `DetachSubjectFromContext` passes `subject.Context` down as the
handler-resolution context for the next level (`LifecycleInterceptor.cs:70,73`), and a property-attached
subject has no other edge, so clearing the link first makes `child.Context` resolve nothing. Grandchildren
would get bookkeeping but no `ILifecycleHandler` invocation, so no `SubjectRegistry` deregistration and no
descent into their own children, and the explicit recursion at `:260-268` cannot rescue it because the
handler-driven descent ran first and consumed the set entry, so it no-ops at `:206-210`. This is the same
invariant the root path states and the first version violated here. `master` is correct only because
`InterceptorExecutor.RemoveFallbackContext` removes the edge after the callbacks
(`InterceptorExecutor.cs:78-85`).

Two precisions from the third review. `SubjectDetaching` is **not** lost: it is raised on the interceptor
instance at `:194` and `:255`, so it does not depend on context resolution. And the damage is wider than
descendants: `DetachSubjectProperty` at `:238` resolves `IPropertyLifecycleHandler` through
`subject.Context` (`LifecycleInterceptorExtensions.cs:60-73`), so an early release also loses the
subject's own per-property deregistration.

Releasing in the `finally` also closes the window in which the subject is unowned while its graph is
still mid-detach, during which another graph could claim it and the remaining work would resolve into the
wrong graph.

**The repoint is removed.** The first version repointed the link at a surviving reference when the
departing one was the link's target. Both reviewers built a pure delegation cycle out of it, one without
any root on the loop, using only supported back-references (#69). It was also non-atomic as specified
(clear-then-set leaves the whole subtree resolving nothing) and picked a hash-order-dependent survivor.
Removing it does not reopen #410, whose mechanism is a detach that leaves property values set and which
the unconditional clear at `count == 0` closes. What it leaves is a milder, distinct case: a multi-parent
subject whose first parent leaves keeps a link to that parent until it fully detaches. Recorded in the
pull request description as a follow-up.

### The reconciliation ledger, and #210

`_lastProcessedValues` is cleaned up in two places today (`LifecycleInterceptor.cs:181`, `:235`) plus a
rollback in `WriteProperty` (`:361`), and the rule that keeps it correct is written nowhere. #210 reports
that an entry orphans when a property is removed from a still-attached subject. That is not reachable:
`IInterceptorSubject.Properties` is an `IReadOnlyDictionary` whose only mutator is `AddProperties`
(`IInterceptorSubject.cs:25,31`), and no removal counterpart exists anywhere.

Rather than leave a trap, the ledger's lifetime is bound to a notification that already exists and that
any removal API would have to fire:

```csharp
void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
{
    if (!change.Property.Metadata.Type.CanContainSubjects()) return;
    lock (_attachedSubjects) _lastProcessedValues.Remove(change.Property);
}
```

The two explicit removes then delete; the `WriteProperty` rollback stays, because that is an undo rather
than a property leaving.

Costs, all recorded: `LifecycleInterceptor` joins `GetServices<IPropertyLifecycleHandler>()` and needs
no-op `AttachProperty` and `RefreshCollectionProperty`; it joins the per-property loop in
`AttachSubjectProperty`/`DetachSubjectProperty` (`LifecycleInterceptorExtensions.cs:45-73`), once per
property per attach and detach; and it joins the `RefreshCollectionProperty` fan-out at
`LifecycleInterceptor.cs:378-382`, calling itself re-entrantly under a lock it already holds.

The stronger fix, storing the value on the property's own data so removal is structural, is rejected: it
sits on the hot write path and per-property data storage is what #222 is trying to eliminate.

## 6. Public API and migration

| Change | Kind |
|---|---|
| `AddFallbackContext` / `RemoveFallbackContext` keep signatures, lose the attach side effect | behavioural |
| `IInterceptorSubject.AttachToContext` in core, `DetachFromContext` in `Namotion.Interceptor.Tracking` | additive; the generated constructor must reach the first without a Tracking reference, and the second needs the reference count |
| `InterceptorExecutor`'s method overrides become `protected virtual` guard hooks called inside the base's critical section | snapshot change, no source break |
| `ContextInheritanceHandler` and `WithContextInheritance()` keep their names, body changes | none |

The parent link, its setters, the owner and the attach-context record are all internal.

`AddTemporaryFallbackContext` was in the first version of this design and is **removed**. Its only
intended users were the three connector sites, and section 2 shows those need the attach rather than the
services. No caller remains, so the API does not exist.

### Call sites

112 calls by `\.(Add|Remove)FallbackContext\(`. Roughly 83 need no change: `ContextDelegationCycleTests`
(46), `ContextConcurrencyTests` (17), `ContextDeepGraphTests` (8) and similar build plain
context-to-context graphs. That project references only `Namotion.Interceptor`,
`Namotion.Interceptor.Testing` and the generator, so no `ILifecycleInterceptor` can be registered there
and every one of those calls is genuinely unaffected. 29 calls across 16 files are subject-facing.

| Site | Becomes |
|---|---|
| `SubjectCodeGenerator.cs:246` | `AttachToContext` |
| `DynamicSubject.cs:15` | `AttachToContext` |
| `RootManager.cs:85` | `AttachToContext` |
| `ContextInheritanceHandler.cs:21,25` | descent trigger |
| `OpcUaSubjectLoader.cs:280` | `AttachToContext` |
| `SubjectUpdateApplier.cs:145` | `AttachToContext` |
| `SubjectItemsUpdateApplier.cs:229` | `AttachToContext` |

The three connector sites therefore keep working exactly as they do today: the item is attached as a root
in the parent's graph, populated while registry-visible, then assigned, at which point the parent link
already names that context so no link is set. `SubjectItemsUpdateApplier.CreateAndApplyItem` returns the
item and leaves assignment to its caller, which the removed scoped API could not have expressed.

## 7. Edge ownership and errors

### Aliased targets and the reverse entry

`_usedByContexts` is a `HashSet`, so a context appears in it once regardless of how many edges point at
it. On `master` that is safe, because `AddFallbackContext` dedups on `Contains` and there is only one kind
of edge, so two edges to the same target cannot exist. Two independent edge kinds make them possible: a
property-attached child holds `Parent == P.Context`, and a consumer then calls
`child.Context.AddFallbackContext(P.Context)`.

Removing either edge would then unregister the sole reverse entry while the other edge still resolves
through it, so invalidation never reaches the child again and its compiled chain silently keeps an
interceptor set the graph no longer has. That is #400's defect 6, the one it rates most serious.

**So unregistration becomes conditional**: after removing any edge targeting `X`, whether explicit,
attach or parent, the reverse entry is dropped only if no remaining edge on this context targets `X`.
Registration stays unconditional and idempotent, which keeps R4's superset property intact: an extra
entry costs a spurious invalidation, a missing one is permanent staleness.

This is the fifth invariant in section 5 and it is new in this design, not inherited.

### Three kinds of edge

| Kind | Created by | Released by | Owner |
|---|---|---|---|
| Attach edge | `AttachToContext` | `DetachFromContext` or the last detach | lifecycle |
| Parent link | `LifecycleInterceptor` at `count == 1` | last detach | lifecycle |
| Explicit fallback | `AddFallbackContext` | the caller, never the library | consumer |

The attach edge and explicit fallbacks share the `FallbackContexts` array; they are told apart by the
attach context the executor records, not by a tag on the array.

### This closes #207 on both paths

The constructor-mismatch path from the body:

```
var child = new Person(rootContext);   // attach edge -> rootContext, owner claimed
parent.Children = [child];             // count 1 -> parent link -> parent.Context
parent.Children = [];                  // count 0 -> clear link, release ownership and attach edge
```

Reproduced in review: `rootContext._usedByContexts` grows 2, 3, 4 over three cycles on `master`, matching
the issue's measured 8,558 entries. After the change it does not grow.

The two-parent path from the issue's first comment, which has no constructor mismatch:

```
p1.Children = [child];   // count 1, registers P1.Context
p2.Children = [child];   // count 2, registers nothing
p1.Children = [];        // count 1, removes nothing
p2.Children = [];        // count 0, removes P2.Context, which the child never had
```

Also reproduced: `p1.Context._usedByContexts` grows 1, 2, 3. A single field cleared at `count == 0`
releases whatever it points at regardless of which property the change names, so this closes with no
repoint. Both paths get their own reproduction test, since they diverge before they converge.

### What throws

| Condition | Result |
|---|---|
| Attaching a subject owned by another graph | `InvalidOperationException`; earlier items of the same batch stay attached |
| `AddFallbackContext` adding a lifecycle-bearing context to a subject that is not lifecycle-attached | `InvalidOperationException` naming `AttachToContext` |
| `RemoveFallbackContext` targeting the attach edge | `InvalidOperationException` naming `DetachFromContext` |
| Delegation cycle on resolution | unchanged |

Exception messages follow the pattern of `CreateDelegationCycleException`
(`InterceptorSubjectContext.cs:454-462`): they name the fix, not just the fault.

### Observability

The owner, the attach context and the parent link are internal, so a consumer's only feedback channel
would be catching exceptions. That is not a contract anyone can program against, so two public read-only
accessors ship with them:

```csharp
public static bool IsAttached(this IInterceptorSubject subject);
public static IInterceptorSubjectContext? TryGetAttachContext(this IInterceptorSubject subject);
```

And `DetachFromContext` aimed at a context that is not the attach context throws rather than silently
returning, which is the same class of mistake as `RemoveFallbackContext` aimed at the attach edge and
deserves the same treatment. `DetachFromContext` distinguishes the two cases before it acts: a subject
attached through a different context throws, and a second concurrent caller returns having called
nothing.

### Handler exceptions

Unchanged. A throwing `ILifecycleHandler` still propagates and still leaves partial bookkeeping, as on
`master`. That is #384 and it stays out, with the caveat in section 11.

## 8. Behaviour changes

The complete list. Anything discovered beyond these twelve is escalated, not absorbed.

1. `AddFallbackContext` stops attaching.
2. `RemoveFallbackContext` stops detaching a root, and throws when aimed at the attach edge.
3. #207 closed on both paths, including the measured leak, through the owned attach edge.
4. #410: the parent link clears unconditionally at reference count zero, rather than only when property
   values happen to have been cleared.
5. Attaching a subject owned by another graph throws instead of half-attaching silently.
6. A throwing detach interceptor no longer prevents the attach edge from being removed.
7. Concurrent `DetachFromContext` calls run the detach interceptors exactly once (#402 defect 2).
8. `LifecycleInterceptor` appears in `GetServices<IPropertyLifecycleHandler>()`.
9. Handlers resolved ahead of `ContextInheritanceHandler`, which today includes `SubjectRegistry` and
    `ParentTrackingHandler`, now see a child whose context resolves the graph. Today it resolves nothing.
10. `DetachFromContext` throws when the subject still holds property references, instead of removing its
    `_attachedSubjects` entry without decrementing the count and stranding both the count and the
    ownership. The underlying behaviour predates this design; making the operation a documented API is
    what obliges us to guard it.
11. Removing an edge unregisters the reverse `_usedByContexts` entry only when no remaining edge on that
    context targets the same context. Unreachable on `master`, where two edges to one target cannot
    exist.
12. A constructor-attached subject stops being an exception to a rule that already exists. An ordinary
    child already loses all interception on full detach today, because `ContextInheritanceHandler.cs:23-26`
    removes the inherited fallback at reference count zero and the subject then has no edges at all. A
    constructor-attached subject keeps its constructor edge and goes on resolving its write interceptors,
    which is exactly #207's leak. After the change both behave the same way. This is the flip side of
    change 3 and the reason the leak closes.

Traversal order is deliberately **not** on this list. Fixing it to top-down was considered and rejected:
the handler-preserving design keeps every order bit-identical for free, so there is no reason to spend the
risk. It stays available as its own change.

## 9. Verification

### Two categories with opposite gates

- **Characterization tests must pass on unmodified `master`.** If one fails on `master`, the test is
  wrong, not the code.
- **Reproduction tests must fail on unmodified `master`.** One per closed issue, from the issue's own
  repro. If one passes, either the issue is already fixed or we have misunderstood it.

The first commit adds tests only, so at that commit the production code is still `master`'s and both
gates are verifiable by checking it out and running the suite.

### Characterization tests

1. Attach and detach event sequences for a three-level graph, capturing both channels.
2. The resolved-position ordering dependency, including the `[RunsBefore]` tie-break that pulls
   `SubjectRegistry` to position 0.
3. The root's own attach fires last.
4. Grandchildren do not attach under `WithLifecycle()` alone.
5. A child's and a grandchild's own context resolve the parent's services, via
   `TryGetService<ISubjectRegistry>()` and `TryGetLifecycleInterceptor()`, asserted after the graph
   settles. This is the audited consumer's one hard requirement and nothing currently tests it.
6. Multi-parent attach-once and detach-once counts.
7. `IPropertyLifecycleHandler` invocation order on property attach and detach.
8. What a handler resolved ahead of `ContextInheritanceHandler` can see from the child's context, which
   is change 11 and must be pinned before it changes.
9. A subject under the connector seeding pattern is registry-visible before assignment, which is the
   assumption the first version of this design broke.

### Each change to its evidence

| # | Pinned by |
|---|---|
| 1 | root attach through `AttachToContext` only |
| 2 | rewritten `SubjectDetaching_FiresForRootSubject_WhenContextRemoved` |
| 3 | both of the issue's repros, each with a weak-reference probe on `_usedByContexts` |
| 4 | detach that leaves property values set |
| 5 | both shapes: parent-to-parent, and root-in-A-then-child-in-B |
| 6 | throwing detach interceptor, assert the edge is gone and a retry works |
| 7 | two threads calling `DetachFromContext`, assert exactly one interceptor pass |
| 8 | characterization test 7 |
| 9 | characterization test 8 |
| 10 | `DetachFromContext` on a subject that is also a child throws, and the reference count is intact afterwards |
| 11 | two edges to one target in either order, remove one, assert the surviving edge still receives invalidation |
| the #207 path | a constructor-attached subject reaching count zero by the property route releases its attach edge, state and ownership |
| plain contexts | `new Person(InterceptorSubjectContext.Create())` attaches, has no owner, and is unaffected by the cross-graph rule |
| 12 | a constructor-attached subject stops resolving interceptors after a full detach |

### Oracles that must not move

Nine `.verified.txt` files live in `Namotion.Interceptor.Tracking.Tests`: seven
`LifecycleInterceptorTests.*`, one derived-property timestamp snapshot, and the public API snapshot.
Eight are ordering oracles. Any movement is a signal to stop, not a snapshot to accept.
`WhenRemovingInterceptors_ThenAllChildrenAreDetached` and its array counterpart are the two the detach
ordering bug would have moved.

Six `PublicApi.verified.txt` files exist repo-wide. Two are expected to change: `Namotion.Interceptor.Tests`
(the executor overrides and the new extensions) and `Namotion.Interceptor.Tracking.Tests` (if
`LifecycleInterceptor`'s interface list is public there).

`ContextConcurrencyFuzzTests` needs its model extended with parent links, a third mutable edge kind under
the same lock and the same R4 discipline.

### New concurrency coverage

Parent link set and clear reuse `AddFallbackContext`'s publish and `_usedByContexts` protocol, so the
fuzz extension covers them. The owner claim is serialized by `lock (_attachedSubjects)` within a graph but
not across graphs, so the owner claim nests the executor's `_mutationLock` inside `_attachedSubjects`:
two threads attaching the same subject to two graphs, exactly one wins, and the loser throws leaving
earlier items of its batch attached, which is asserted rather than assumed. Plus a directed test that two
concurrent `DetachFromContext` calls run the interceptors exactly once. Plus the two
rendezvous tests for changes 8 and 9, a directed test that the `AddFallbackContext` guard and the
`Detaching` transition are serialised on `_mutationLock`, and a test that a handler which re-attaches a
subject during its own detach survives the `finally`.

### Mutants that must die

Restore `IsContextAttach` to the link gate; release the parent link before the handlers instead of in the
`finally`; delete the owner check; delete the `finally` on the detach edge removal; delete the
self-context guard; delete the `if (!AddFallbackContext(...)) return` guard; reintroduce the repoint; set
the link and release the attach edge instead of skipping the link; trust the captured `count == 0` in the
detach `finally` instead of re-reading `_attachedSubjects`; release `_owner` from a place that reads the
reference count without holding `_attachedSubjects`; make the reverse-entry unregistration unconditional
again; route the detach cleanup through the public `RemoveFallbackContext`; drop the reference-count
guard on `DetachFromContext`; drop the last-property-detach release of the attach edge; make the
lifecycle-bearing guard test "attach state is null" instead of "not lifecycle-attached".

Each must fail its corresponding test. Everything from "reintroduce the repoint" onward is a mutant
precisely because a review found it and an earlier version of this design did not, so a test that does
not catch it is not testing what we think it is.

## 10. Staging

One pull request on `design/context-inheritance-parent-link`, built from commits in this order.

| Commit | Content | Gate |
|---|---|---|
| 1 | Characterization tests only | green with `master`'s production code |
| 2 | Reproduction tests expressible against `master`'s API: #207 both paths, #410 symptom 2, #402 defect 1 | **red**, each for its issue's stated reason |
| 3 | `AttachToContext` / `DetachFromContext`, migrate the seven production call sites | behaviour-neutral |
| 4 | `ContextState.Parent`, internal setters, `LifecycleInterceptor` sets and clears it with both guards, handler body becomes the descent trigger | snapshots must not move; #410 turns green |
| 5 | Replace the executor's method overrides with guard hooks called inside the base's critical section, add the loud errors and the observability accessors | the breaking one; the guards must not be lost with the overrides |
| 6 | The attach-context record and the owner, both under their stated locks, the state-re-reading `finally`, conditional reverse-entry unregistration | #207 turns green, #402 turns green except defect 1 |
| 7 | Reproduction tests for the shapes that need the new API (two-graph, rendezvous) | red before their commit, green after |
| 8 | The #210 fix | characterization test 7 records the new handler entry |
| 9 | Consumer and design docs | see below |

Commit 2 carries only the repros expressible against `master`'s API; the two-graph and rendezvous shapes
need `AttachToContext` and move to commit 7, where the red-then-green gate is per-commit rather than
against `master`.

Commit 3's behaviour-neutrality holds because `AttachSubjectToContext` is idempotent through
`_attachedSubjects`, at the cost of re-seeding `_lastProcessedValues` and walking the subtree twice for
the life of that commit. Worth knowing before reading a benchmark taken there.

If review stalls, the cut line is between commits 4 and 5: everything up to and including 4 is
behaviour-neutral for existing callers.

### Benchmark gates

| After | Benchmarks | Why |
|---|---|---|
| Commit 4 | `RegistryBenchmark`, `ContextDelegationDepthBenchmark` | The `ImmutableArray`-to-field trade predicts one fewer allocation and 24 fewer bytes per attached subject. Also a correctness signal: if allocations do not drop, the link is not replacing the edge. The depth benchmark guards the delegation fast path, which changes shape from one fallback to one parent. |
| Commit 6 | `RegistryBenchmark` | The owner and attach state are two reference fields on `InterceptorExecutor`, so 16 bytes on an object that already exists per subject and no new allocation. Expected neutral. Measured because it is the only commit that adds work to the attach path, in the form of one extra `_mutationLock` acquisition per attach and detach. |
| Branch head | Both, against `master` | The numbers for the pull request description. |

Run through `scripts/benchmark.ps1` with multiple launches: #412 recorded a single launch on a busy
machine inverting its own result.

### Documentation

Consumer-facing: `docs/interceptor.md`, `docs/tracking.md`, `docs/dynamic.md` (three code samples at
`:55`, `:102`, `:123`) and `docs/generator.md` (`:48`), all of which currently teach `AddFallbackContext`
as the attach mechanism.

- the three kinds of edge and who owns each
- `AttachToContext` / `DetachFromContext` as the way a root joins and leaves a graph
- `IsAttached` and `TryGetAttachContext`
- one graph per subject, and what each exception means

Design-facing, `docs/design/tracking-lifecycle.md`:

- the parent link, its ownership, the `count == 1` gate and both guards
- that `_lastProcessedValues` entries live exactly as long as their property is attached, replacing the
  now-stale "Removed on detach" table of three locations
- the lock ordering section, which states two locks and after this change must state
  `_attachedSubjects -> _mutationLock -> _usedByContexts`, with `_mutationLock -> user code ->
  _attachedSubjects` as the edge that closes the cycle (#404, pre-existing, not made worse here)
- the resolved-position ordering dependency from section 2, which this design preserves and no issue
  records
- the global versus per-graph reference count distinction, and why one graph per subject collapses it

### Follow-ups, to be filed or updated with the issues above

Both are consequences of option B, which deliberately stops short of making the two composite root
operations atomic against each other. Recorded so the decision is visible rather than inferred from what
the design does not say.

1. **Serialise `AttachToContext` and `DetachFromContext` per subject.** Closes #402 defect 1 and #411 by
   making them not race, rather than by negotiating mid-flight. Viable only because this design removes
   user code from the edge mutation, and it needs one documented contract: a lifecycle handler must not
   call either method, since that is the one path that could deadlock against `LifecycleInterceptor`'s
   monitor. Small, and its whole surface is two methods. Goes on #402 and #411.
2. **The deferred handoff** from #411's own comment is the only thing that makes a concurrent add
   transparent instead of serialised. It is PR #412's machinery and should be built only if follow-up 1
   proves insufficient. Roots are attached at startup and detached at shutdown, so that is unlikely.

### PR #412

Closed unmerged rather than reverted, since it never landed on `master`. Two things carry forward: the
`TryPublishFallbackContext` / `TryUnpublishFallbackContext` extraction, and the corrected XML
documentation on `IInterceptorSubjectContext`.

### Final step, human gated

Updating issues and merging are **not** part of the implementation plan and are not done autonomously.
The plan ends at commits and pushes to this branch. Each row below was checked against the issue's
comments, not only its body.

| Issue | Action |
|---|---|
| #402 | **Update, keep open on defect 1 only.** Defects 3, 4 and 5 follow from there being no callbacks inside the edge mutation. Defect 2 is closed by change 7, the single clear-under-lock that makes detach run exactly once. Defect 1, a remove racing an add, stays at `master`'s behaviour, because `AttachToContext` and `DetachFromContext` are two-step operations and are not atomic against each other. It is narrowed from every child detach to an explicit concurrent root operation, since `ContextInheritanceHandler` no longer calls the public mutators. Follow-up 1 closes it. Its first comment concluded that "the complete fix needs a decision about where lifecycle callbacks run, not just a reordering", which is what this design decides. |
| #207 | Close, citing both reproductions, both verified during review. |
| #410 | Close symptom 2 and the retention. Symptom 1 only if the reproduction attempt succeeds; if it cannot be built, strike it with the reasoning recorded. |
| #210 | Close as not reachable, noting that commit 8 makes it structurally impossible if a removal API lands later. |
| #411 | **Update, keep open.** Narrowed the same way as #402 defect 1 and for the same reason, and closed by the same follow-up. An earlier version of this design added a loud rejection for it; that was part of the protocol option B removes, and serialising the two operations closes it properly rather than turning it into an exception the caller must handle. |
| #384 | Update, narrowed. The attach-tail case loses route 2 with #412's deferred handoff; route 1 is pre-existing. The detach-half case still raises on a cyclic chain, but such a chain now requires a consumer to have built one deliberately. Its stated blocker is removed. See section 11. |
| #412 | Close unmerged, referencing this design. |

Two findings from section 2 have no issue and none is being filed: the two-graph half-support, closed by
change 5, and the resolved-position ordering dependency, preserved deliberately. Both live here and in
`docs/design/tracking-lifecycle.md`, and the pull request description records them, along with the
multi-parent stale-link case left behind by removing the repoint.

## 11. Out of scope

- **#384**, handler exception recovery. Its stated blocker dissolves, since inheritance is no longer a
  handler, and the remaining fix is rollback on throw inside `AttachToProperty`. One caveat found in
  review: change 5 makes `AttachToProperty` throw where it previously could not. A rejected cross-graph
  attach therefore leaves the property value written, the outer interceptors' post-`next` work skipped,
  and earlier items of the same batch attached. An earlier version tried to prevent the last of those
  with a batch-level check, which review refuted because hoisting the check separates it from the claim.
  The partial batch is stated rather than fixed, and it is #384's shape.
- **#403, #404, #406**, the `TryAddService` factory under the mutation lock and service equality. Same
  class of problem, different call site. #404's lock edge is documented here but not fixed.
- **#409**, measuring the copy-on-write memory trade-offs.
- **Fixing the traversal order to top-down.** See section 8.
- **Multi-graph support.** See section 12.
- **The multi-parent stale link.** With the repoint removed, a subject whose first parent leaves keeps a
  link to it until full detach. Distinct from #410's mechanism, milder, and recorded as a follow-up.
- **Moving the reference count off `subject.Data`.** `IncrementReferenceCount` and
  `DecrementReferenceCount` do a `ConcurrentDictionary.AddOrUpdate` with a `(string?, string)` tuple key
  on every attach and every detach (`LifecycleInterceptorExtensions.cs:20-43`), for what is a per-subject
  integer. By the placement rule below it belongs on the executor, and on the attach-heavy benchmarks it
  is plausibly a larger win than anything this design does. Out of scope because, unlike the owner, the
  reference count is a Tracking concept and hosting it on a core object needs its own argument. Named
  here so the reasoning is not lost.

### Placement rule

Recorded because the executor is the path of least resistance for per-subject state, being the only typed
store an `IInterceptorSubject` implementation does not have to provide, and without a rule the next three
additions land there by default:

> Per-subject state belongs on `InterceptorExecutor` only when it is both about the subject's position in
> the context or lifecycle graph, and read on a path where a `subject.Data` lookup would show. Everything
> else goes in `subject.Data`.

The owner and the attach context pass both tests. So would the reference count, which is why it is named
above rather than treated as an unrelated optimisation.

## 12. Rejected alternatives

**Running the external consumer's suite as a merge gate.** It would add a build dependency on a
repository this one does not control, for coverage of behaviours nobody has written down. The one
requirement it protected is characterization test 5.

**The repoint on partial detach.** Removed after both reviewers built a pure delegation cycle from it.
See section 5.

**`AddTemporaryFallbackContext`.** Removed once section 2 established that its only intended callers need
the attach rather than the services.

**Storing the reconciliation ledger on the property.** Hot write path, and #222 is trying to remove
per-property data storage.

**Genuine multi-graph support.** It would require: graph identity threaded through the registry, path,
connector and source APIs, because `TryGetService` throws when two services of a type resolve
(`InterceptorSubjectContext.cs:223-232`) and `TryGetRegisteredSubject` resolves the registry that way; a
merged interceptor chain whose interleaving between two graphs is undetermined and where short-circuiting
interceptors decide for both; the return of delegation cycles, since building graph 1 with A above Q and
graph 2 with Q above A makes each the other's parent; ownership tags on every link; and a permanent
per-graph versus total reference count distinction. If it is ever wanted it should be a deliberate design
with graph identity as a first-class parameter, and the exception added by change 5 is what makes that
discoverable instead of silent.
