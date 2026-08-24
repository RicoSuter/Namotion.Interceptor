# Post-Review Gaps: Design

Three independent defects left open after the single-context lifecycle work and its five-reviewer pass. They share no design decision and no code, so they are grouped only because they ship together in the same pull request.

## 1. DI-constructed subjects are silently detached

### The problem

The generator emits the `X(IInterceptorSubjectContext)` constructor only when the type has or will have a parameterless constructor, because that constructor chains `: this()` (`SubjectCodeGenerator.cs:285`, gated by `HasOrWillHaveParameterlessConstructor` in `SubjectMetadataExtractor.cs:814-828`). A type whose only constructor takes dependencies therefore gets no context-taking constructor at all, and dependency injection hands back a permanently detached subject: `anchor=None`, `context=null`, no lifecycle, no registry entry, no hosted services.

Nothing reports this. It is not a compile error, not a runtime exception, and not a log line. The subject simply does nothing.

This gap predates the rewrite, but master papered over it: `RootManager` called `Root.Context.AddFallbackContext(_context)` to bridge a detached root into the graph. The rewrite removed `AddFallbackContext` along with the whole fallback model, which turned a working workaround into a silent failure. It broke HomeBlaze on its ordinary startup path, and no unit test could see it, because the failure is an absence rather than an error.

### The decision

**Mirror every declared constructor with an appended context parameter.** For each constructor the author declares, emit a second one taking `IInterceptorSubjectContext` as a trailing parameter, chaining to the original and attaching:

```csharp
public Foo(A a, B b, IInterceptorSubjectContext context) : this(a, b)
{
    InterceptorSubjectExtensions.AttachToContext(this, context, SubjectAnchorKind.Provisional);
}
```

Rules:

- Same accessibility as the constructor it mirrors.
- `SubjectAnchorKind.Provisional`, matching the existing parameterless path. A subject built through a constructor is a root until a graph adopts it.
- Skipped when the author already declares that exact signature, so a hand-written context constructor always wins.
- The existing parameterless path is unchanged.

This removes the footgun rather than warning about it. `ActivatorUtilities` selects the constructor with the most parameters it can satisfy, so wherever the context is a registered service, DI now picks the mirrored constructor and the subject attaches by itself. That is precisely the case that broke HomeBlaze.

One carve-out: the mirror drops optional parameter defaults, because the metadata does not capture them. When an optional parameter's type is not registered, `ActivatorUtilities` cannot satisfy the mirror and falls back to the original constructor, and the subject is silently detached again, which is the very failure the mirror exists to prevent. The fix therefore holds only where every constructor parameter is resolvable from the container.

A compile-time diagnostic was considered and rejected as the primary fix: the generator cannot tell whether an author intends to attach explicitly, or never to attach at all, so the diagnostic would fire on correct code. It would be noisy at warning level and ignored at info level, and it would fix nothing.

### Consequences

Generated subjects with parameterized constructors gain public constructors, so the public API snapshots change. This is additive: no existing call site changes meaning, and no existing constructor is removed or altered.

It also changes which constructor DI selects for such subjects, from the parameterless-or-explicit one to the mirrored one. That is the intended behaviour change, and it is what makes the fix a fix. A consumer that deliberately wants a detached subject under DI must now say so, by constructing it directly rather than through the container.

## 2. A late-registered lifecycle can invert the lock order

### The problem

`InterceptorExecutor.SetStructuralPropertyValue` resolves its routing decision from a lock-free read of the attachment state. When that yields no lifecycle, it takes the attachment monitor alone and proceeds to the write, which resolves the interceptor chain from a *fresh* `Volatile.Read(ref _state)`.

A `LifecycleInterceptor` registered between those two points is therefore in the chain but not in the routing decision. Its `WriteProperty` then takes the lifecycle gate while the attachment monitor is already held and the gate is not, which is the exact inversion the documented total order forbids: gate, then attachment monitor, then `SyncRoot`. A concurrent structural write holding the gate and reaching `ReleaseClaim` on the same subject closes the cycle.

The code comment at the routing site states the assumption that a lifecycle registered after the resolution is not seen by this write. That assumption is false, because the chain is not pinned to the state the routing decision read.

### The decision

**Pin the chain to the routing snapshot.** Resolve the interceptor chain from the same context state the routing decision used, so the two cannot disagree. A lifecycle registered after that read is then invisible to both, which is the behaviour the comment already claims and the lock order already assumes.

The alternative, re-checking the routing after resolving the chain, was rejected: it turns one inconsistency into a retry, and the window can reopen on every pass.

## 3. Retry is unbounded, and that is acceptable

### The problem

The structural write path uses bare `while (true)` retry loops with no backoff and no attempt cap. Design decision 5 of the single-context spec says the opposite: "Ordering is preferred over retry, which can livelock under sustained attach churn." Code and specification disagree, and a reader cannot tell which is authoritative.

### The decision

**The code is right and the specification is wrong.** Correct decision 5. Do not change the write protocol.

The loop is **livelock-free**: every retry is caused by another thread completing an attachment transition, and each transition requires the gate, so a retry is evidence of progress elsewhere rather than of mutual blocking.

It is **not starvation-free**, and no bound is enforced. In principle a writer could retry indefinitely while other threads keep transitioning the same subject's attachment. That needs sustained attach and detach churn on one subject, concurrent with structural writes to that same subject, which is a pathological workload rather than a plausible one. The lock-order tests drive the loop at 3,000 iterations without observing it.

If it is ever observed, the mitigation is to order rather than retry, which is a rework of the write protocol and not a tuning change.

This distinction belongs in `docs/design/tracking-lifecycle.md` and nowhere else. It is an internal invariant: a consumer cannot act on it, it never surfaces in practice, and putting it in user-facing documentation or release notes would alarm without informing.

## 4. An orphaned null baseline in property admission

### The problem

`PropertyAdmission.Admit` commits a null baseline directly (`graph.SetBaseline(property, null)`) rather than going through `StructuralReconciler.Reconcile`. The non-null branch goes through the reconciler and is therefore covered by the ownership check added at its entry, which returns without committing when the writing subject has been released. The direct null branch bypasses that check.

A user getter invoked by `CaptureStructuralValues` runs at callback depth zero and can release the admitting subject. The admission then writes one baseline entry for a subject the graph no longer owns, and nothing ever removes it.

No stranding is possible on this path, because a null value attaches nothing. The residue is a single stale dictionary entry.

### The decision

**Apply the same ownership check before the direct `SetBaseline`.** One condition, matching the reconciler's, so both admission branches behave alike.

This is the smallest of the three fixes and the least reachable. It is included because it is a known defect in code this work introduced, and leaving a known hole to save one line is a poor trade.

## Testing

Each fix needs a test that fails without it:

- **Generator**: a subject whose only constructor takes parameters, constructed through `ActivatorUtilities` with the context registered, is attached afterwards. Plus a subject that already declares the mirrored signature, proving the generator skips rather than emits a duplicate. Public API snapshots accepted deliberately, with the diff inspected rather than rubber-stamped.
- **Lock order**: the existing lock-order tests inject inversions; extend them with a lifecycle registered between the routing read and the chain resolution. If that window cannot be hit deterministically without production hooks, say so rather than writing a timing-dependent test, and pin the fix structurally instead by asserting the chain and routing derive from one state read.
- **Admission**: a getter that releases the admitting subject during `CaptureStructuralValues`, asserting no baseline entry survives for it.
