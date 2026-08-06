# Registration-scoped subject initializers

Status: approved, not yet implemented
Issue: #430
Supersedes the approach in PR #429

## Problem

Three separate extension points add properties to a subject while it attaches:

| Extension point | Interface | Example |
|---|---|---|
| property initializer | `ISubjectPropertyInitializer` | `UnitAttribute` adds a `Unit` attribute |
| property lifecycle handler | `IPropertyLifecycleHandler` | `PropertyAttributeInitializer` adds `State` and `Configuration` |
| subject lifecycle handler | `ILifecycleHandler` | `MethodPropertyInitializer` adds a property per `[Operation]` method |

All three re-run whenever a subject re-attaches, which an ordinary move between parents
triggers as soon as the old reference is removed before the new one is added. Their effect
does not re-run with them: the properties they added live on `subject.Properties` and
survive detach. The second run therefore adds a property that is already there, and
`RegisteredSubject.AddProperty` throws.

### The re-run is redundant, not merely unsafe

`RegisteredSubject`'s constructor rebuilds its property set from `subject.Properties`
(`RegisteredSubject.cs:143`), which still holds everything the previous run added. On
re-attach the entire prior effect is already present before any initializer runs.

`LifecycleInterceptor.AttachToContext` snapshots `subject.Properties.Keys` before invoking
handlers, and on re-attach that snapshot already contains the dynamic properties. So the
second run does not repeat the first, it runs every initializer over a strictly larger
property set, including the properties the initializers themselves created.

### Guarding makes it worse

The documented workaround is for each implementation to check before adding. Measured, that
converts a loud failure into a silent one:

```
initializer invocations:                    2
capture#1 Parent == live registration:      False
capture#1 parent still in KnownSubjects:    False
capture#1 sees marker:                      True
```

An initializer may capture the `RegisteredSubjectProperty` it was handed. `TryGetAttribute`
resolves through `Parent.TryGetPropertyAttribute(...)` (`RegisteredSubjectProperty.cs:301`),
and `Parent` is the `RegisteredSubject` captured at the time. `SubjectRegistry` builds a new
one on every re-attach, so a guarded initializer keeps its first-run attribute alive with a
getter that answers from a discarded registration instead of throwing.

This is not hypothetical. A downstream initializer creates a mutable cell in a closure and
wires a getter and setter to it, capturing `property` in both.

### Root cause

`SubjectRegistry` discards `RegisteredSubject` on detach and rebuilds it on re-attach.
Everything above follows from that one decision. Re-invocation is an artifact of the
rebuild, not a use case: no implementation found across this repository, HomeBlaze, the
downstream consumers or the docs benefits from running twice. Metadata that must change over
time is already served by derived attributes, whose getters re-evaluate on read.

## Decision

Split by what the state belongs to.

| Layer | Owns | Scope |
|---|---|---|
| Registry | the structure of a subject | once per subject |
| Lifecycle | a subject's participation in a graph | once per attach and per detach |

`InitializeProperty` means what its name says: once per property, ever.

## Design

### 1. Retain `RegisteredSubject`

Pin it on the subject rather than in the registry alone, create it once, and reuse it.
`SubjectRegistry` still adds and removes `_knownSubjects` entries on attach and detach, so
attached-ness is unchanged. It stops discarding the object. The registration dies with the
subject, so nothing leaks.

Verified safe: detach already empties every piece of graph-scoped state the object holds.

```
attached: middle parents=1  leaf parents=1  middle.Child children=1
detached: middle parents=0  leaf parents=0  middle.Child children=0
detached: refcount middle=0 leaf=0
detached: middle in KnownSubjects=False
detached: properties retained middle=2 leaf=1
```

At the moment of discard the object is already in the state a freshly built one would be in,
except that it still knows its properties. That is the part worth keeping.

The registration is shared across contexts. A subject moved from context A to context B keeps
A's registration, so an initializer that B has and A did not will not run for it. This is
accepted: B already inherits A's added properties either way, so a per-registry registration
would produce a half-initialized merge rather than a clean slate.

### 2. Initialize once per property

A flag on `RegisteredSubjectProperty`, set after its initializers have run. `SubjectRegistry`
keeps invoking them from `IPropertyLifecycleHandler.AttachProperty` and only guards the call.

Invocation is deliberately not moved to registration time. The existing ancestor-resolution
tests pin that a derived attribute's first getter evaluation already sees the full ancestor
chain, and that ordering should not move in the same change.

### 3. Reconcile on re-registration

Any property present on `subject.Properties` that the retained registration does not know
about is added to it and initialized. `DynamicSubjectFactory.cs:49` calls
`subject.AddProperties(...)` directly, bypassing the registry, and the current rebuild picks
those up for free. Retention alone would not.

### 4. Add `ISubjectInitializer`

```csharp
public interface ISubjectInitializer
{
    void InitializeSubject(RegisteredSubject subject);
}
```

Called once per `RegisteredSubject`. This is the missing grain size: `ISubjectPropertyInitializer`
is per property, and the only per-subject hook is `ILifecycleHandler`, which is episodic by
design. Work that is genuinely per-subject-once had nowhere correct to live, which is why
`MethodPropertyInitializer` is an `ILifecycleHandler`.

The only new public type in this design.

### 5. `AddProperty` keeps throwing on a duplicate

Under once-semantics a duplicate is a real error again rather than an artifact of re-attach,
so the throw is the correct behaviour and stays.

## Where each member lands

### Registry layer

| Interface | Runs | Implementations |
|---|---|---|
| `ISubjectPropertyInitializer` | once per (`RegisteredSubject`, property) | `UnitAttribute` (SampleWeb), `DefaultValueInitializer` (docs), `PropertyAttributeInitializer` (moved in), downstream attribute and service initializers |
| `ISubjectInitializer` (new) | once per `RegisteredSubject` | `MethodPropertyInitializer` (moved in) |

### Lifecycle layer

| Interface | Runs | Implementations |
|---|---|---|
| `ILifecycleHandler` | every attach and detach | `ContextInheritanceHandler`, `ParentTrackingHandler`, `SubjectRegistry`, `HostedServiceHandler`, `SubjectPathResolver`, `TestLifecycleHandler`, `LogPropertyChangesHandler` (sample) |
| `IPropertyLifecycleHandler` | every attach and detach | `SubjectRegistry`, `DerivedPropertyChangeHandler`, `TestLifecycleHandler` |

Everything remaining in the lifecycle layer is paired: it starts what detach stops, or it
maintains graph state that detach empties, or it is deliberately fire-every-time.

## Callback signatures stay as they are

Considered and rejected: passing richer context so handlers could detect re-attach and defend
against it. Fixing the scope removes the need to detect anything, and adding the flags anyway
would preserve the shape of a problem that has been deleted.

The registry callbacks lose nothing by staying as they are. Everything reachable from
`RegisteredSubjectProperty` includes `Parent` and the whole registration, `Reference.Metadata`
with `IsDynamic`, `IsDerived`, `IsIntercepted`, `IsPublic` and `PropertyInfo`,
`ReflectionAttributes`, and `Subject.Context`. `SubjectRegistry.AttachProperty` holds only
`change.Subject` and `change.Property`, both derivable from the parameter.

Two gaps in the lifecycle layer are recorded and deferred, neither having a consumer today:

- `SubjectLifecycleChange` drops the attaching context, which for a child is the parent's
  context rather than `change.Subject.Context`.
- `SubjectPropertyLifecycleChange` cannot distinguish a subject-wide attach sweep from a
  standalone `AddProperty` on an already-attached subject.

Deferring costs nothing for the first: `SubjectLifecycleChange` is a `readonly struct` with
`init` properties, and handlers only read it, so a new non-required property is source
compatible.

Constraint for the second: `SubjectPropertyLifecycleChange` is a positional `record struct`,
so a new positional parameter would break its constructor. Any future field must be added as
an `init` property.

## Concurrency

`AddProperty` is public and may be called concurrently, at runtime, on a subject that is being
actively read and written. The design must hold for that, not only for the initializer path.

### One lock, not two

`AddProperty` locks on `subject.SyncRoot`. `_addPropertyLock` from PR #429 is removed.

`SyncRoot` is already the lock guarding a subject's property dictionary, identically in both
implementations (`SubjectCodeGenerator.cs:155` and `DynamicSubject.cs:39`):

```csharp
void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
{
    lock (((IInterceptorSubject)this).SyncRoot) { _properties = ...ToFrozenDictionary(); }
}
```

A separate lock guards only the registry's copy, leaving the subject's copy free to be mutated
concurrently through `subject.AddProperties(...)`, which `DynamicSubjectFactory` does. The
check and the add must be atomic across both views, so `SyncRoot` is required for correctness
rather than chosen for convenience.

### Lock graph

```
SyncRoot  ->  RegisteredSubject._lock     (parent state; leaf, never calls back into the subject)
```

Acyclic. `SyncRoot` is a plain `object`, so `lock` is `Monitor` and re-entrant, which resolves
the cycle recorded in `f95dde99`: a getter or setter running under `SyncRoot` that calls
`AddProperty` re-enters instead of deadlocking.

### Cost

`AddProperty` is not on the hot path, so correctness, thread safety and freedom from deadlock
decide the design and performance is optimised only within what those allow.

Holding `SyncRoot` across the registry's property rebuild blocks intercepted reads and writes
on that subject (`ReadInterceptorFactory.cs:19`, `WriteInterceptorFactory.cs:19`). The delta is
bounded: both `AddProperties` implementations already perform an O(n) rebuild inside
`lock (SyncRoot)`, so this makes the critical section roughly twice as long rather than
introducing one.

Cheap work that does not need the lock still stays outside it: the property metadata and the
`RegisteredSubjectProperty` are built before it is taken. Building the frozen dictionary outside
the lock as well, and swapping it under the lock with a re-validation retry, would return to
roughly one rebuild. Recorded as available, not planned, because it trades a straight-line
critical section for a retry loop to optimise a path that is not hot.

### Mutate under the lock, notify outside it

`Subject.AttachSubjectProperty(...)` and `SetPropertyValueWithInterception` stay outside the
lock. Both invoke handler and initializer code that can do anything, and holding `SyncRoot`
across it reintroduces the hazard this removes.

### Reject an add from inside an accessor

Calling `AddProperty` from a getter or setter of the same subject leaves that section running
under the caller's lock. Re-entrancy prevents a deadlock but cannot prevent the situation, so
`AddProperty` rejects it before taking the lock:

```csharp
if (Monitor.IsEntered(Subject.SyncRoot))
    throw new InvalidOperationException(
        $"Cannot add property '{name}' to '{Subject.GetType().Name}' from a getter or setter of " +
        "the same subject: the accessor already holds the subject's lock.");
```

The check is not racy, because it is not the question that races. `Monitor.IsEntered` asks
whether the *current thread* holds the lock, which is thread-local: no other thread can change
the answer, and this thread does nothing between the check and the `lock`. The racy question
would be whether the lock is free, which is not asked.

Take-or-throw does not work as an alternative. `Monitor` is re-entrant, so
`Monitor.TryEnter(SyncRoot, 0)` returns true when the current thread already owns the lock,
which is exactly the case to reject. A non-re-entrant primitive such as `SemaphoreSlim(1,1)`
would block forever there rather than throw, turning a diagnosable error into the deadlock, and
it cannot replace `SyncRoot` in any case because `subject.AddProperties` takes `SyncRoot` and
correctness requires one lock across both views.

Measured cost, Release build: `Monitor.IsEntered` is 1.56 ns when the lock is not held and
1.76 ns when it is, against 2309 ns for a single `ToFrozenDictionary` rebuild of a 12-property
subject. `AddProperty` performs two such rebuilds, so the check is about 0.03% of one of them.

The check is precise because `SyncRoot` is held only around a user accessor. The read and write
terminals take it around `innerReadValue` and `innerWriteValue` (`ReadInterceptorFactory.cs:19`,
`WriteInterceptorFactory.cs:19`), and the zero-interceptor terminal does not take it at all.

No library flow that adds properties holds it. `LifecycleInterceptor.WriteProperty` calls
`next(ref context)` first, and the terminal takes and releases `SyncRoot` within that call, so
all attach work including every initializer runs afterwards with no lock held. That holds for a
self-referencing subject too, which is the case most likely to look like a false positive.

Not detectable this way, and recorded rather than solved: a cross-subject cycle where A's getter
adds a property to B while B's getter adds one to A. `Monitor.IsEntered` answers only for the
current thread and the subject being added to.

## Consumer migration

Two HomeBlaze classes change interface. Both are registered as one-line context services in
`SubjectContextFactory.cs:31-36`, so each is an interface change plus a registration type
argument.

| Class | From | To |
|---|---|---|
| `MethodPropertyInitializer` | `ILifecycleHandler` | `ISubjectInitializer` |
| `PropertyAttributeInitializer` | `IPropertyLifecycleHandler` | `ISubjectPropertyInitializer` |

Both get shorter, which is the signal that the interface was wrong rather than the code.
`MethodPropertyInitializer` loses its `IsContextAttach` wrapper, its
`TryGetRegisteredSubject() ?? throw` lookup, and the re-attach guard added in PR #429.
`PropertyAttributeInitializer` loses its `TryGetRegisteredProperty` lookup and null check, two
empty interface stubs, and both `TryGetAttribute(...) is null` guards.

`HomeBlaze.Services/Lifecycle/` then holds no lifecycle handlers and should be renamed to
`Initializers/`.

No public API breaks. `ISubjectPropertyInitializer` keeps its signature, so no downstream
implementation must change. Existing guards become redundant and stay harmless, and the guard
added downstream for PR #429 can be reverted.

## Testing

- An initializer runs exactly once across a move between parents that passes the reference
  count through zero.
- A captured `RegisteredSubjectProperty` still resolves against the live registration after
  re-attach, which is the stale-read defect above and fails against current code.
- A property added to a detached subject through `subject.AddProperties(...)` is picked up and
  initialized on re-registration.
- `AddProperty` still throws on a genuine duplicate.
- A concurrent `AddProperty` and `subject.AddProperties(...)` on the same subject leave the
  registry's and the subject's property sets in agreement.
- `AddProperty` called from a property getter completes rather than deadlocking.
- The existing ancestor-resolution tests continue to pass unchanged, pinning that initializer
  invocation ordering did not move.

## Out of scope

- **PR #419.** Open and unmerged, and it reworks this area heavily. This design targets
  `master`. #419 confirms that simultaneous membership in two graphs throws while a sequential
  move between contexts stays supported, which is what makes the shared-registration decision
  in section 1 a real trade rather than a theoretical one. It also moves the reference count off
  `subject.Data` onto `InterceptorExecutor`; if these land in that order, the retained
  registration should follow it there.
- **Property removal (#210).** No removal API exists. Retention makes one unnecessary here,
  but a future removal API must clear the retained registration's entry.
- **PR #429.** One part survives: the fix for `AddProperty` corrupting a declared property's
  metadata when a rejected add had already called `Subject.AddProperties`. Its locking is
  replaced rather than kept, since `_addPropertyLock` guards only one of the two views. Its
  idempotency documentation, its `ISubjectPropertyInitializer` remarks, and the consumer guards
  it added all come out.

## Permanent home

This spec is temporary. On implementation, fold the layer split and the concurrency rules into
`docs/design/tracking-lifecycle.md` and update `docs/registry.md`, whose two initializer
samples currently teach the guard pattern this removes.
