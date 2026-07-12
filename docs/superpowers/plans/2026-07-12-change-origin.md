# Typed ChangeOrigin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ambient source scope with a one-shot per-write origin stamp and a typed `ChangeOrigin` discriminator, then add the `Correction` kind for equality-suppressed inbound values.

**Architecture:** Origin is armed per write in a thread-static slot, consumed by the matching `PropertyWriteContext` at construction, finalized at the terminal write (survival check: stored value must equal sent value), and published on `SubjectPropertyChange.Origin`. Timestamps stay ambient. Spec: `docs/superpowers/specs/2026-07-12-change-origin-design.md`.

**Tech Stack:** .NET 9 / netstandard2.0 core, xUnit, Verify (public API snapshots), BenchmarkDotNet.

## Global Constraints

- Two phases, one PR each. Phase 1 is implemented on the existing branch `feature/change-origin-design` (PR 1 targets `master`, closes #345). Phase 2 is implemented on a new branch `feature/change-origin-corrections` created off `feature/change-origin-design` (PR 2 targets `feature/change-origin-design`; the user retargets it to `master` after merging PR 1, closes #365).
- Test naming: `When<Condition>_Then<ExpectedBehavior>`, with `// Arrange`, `// Act`, `// Assert` comments.
- No hardcoded waits in tests. Use existing helpers or synchronous APIs.
- No em dashes in docs or PR descriptions. No AI attribution in commits or PRs.
- Build must stay warning-free (warnings as errors).
- Run unit tests with `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`. Run targeted projects while iterating.
- `ChangeOriginKind` stays byte-backed. `default(ChangeOrigin)` must remain `Local`.
- Public API snapshots: when `VerifyChecksTests.PublicApi` fails intentionally, copy the `.received.txt` over the `.verified.txt`.

---

# Phase 1 (PR 1): mechanism swap, typed kinds, validator provenance

### Task 1: ChangeOrigin core types

**Files:**
- Create: `src/Namotion.Interceptor/ChangeOrigin.cs`
- Test: `src/Namotion.Interceptor.Tests/ChangeOriginTests.cs`

**Interfaces:**
- Produces: `ChangeOriginKind : byte { Local = 0, FromSource = 1, Confirmed = 2 }`; `readonly struct ChangeOrigin { ChangeOriginKind Kind; object? Source; }` with `static ChangeOrigin Local => default`, `static ChangeOrigin FromSource(object source)`, `static ChangeOrigin Confirmed(object source)`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Namotion.Interceptor.Tests;

public class ChangeOriginTests
{
    [Fact]
    public void WhenDefault_ThenKindIsLocalAndSourceIsNull()
    {
        // Arrange & Act
        var origin = default(ChangeOrigin);

        // Assert
        Assert.Equal(ChangeOriginKind.Local, origin.Kind);
        Assert.Null(origin.Source);
    }

    [Fact]
    public void WhenFromSource_ThenKindAndSourceAreSet()
    {
        // Arrange
        var source = new object();

        // Act
        var origin = ChangeOrigin.FromSource(source);

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, origin.Kind);
        Assert.Same(source, origin.Source);
    }

    [Fact]
    public void WhenConfirmed_ThenKindAndSourceAreSet()
    {
        // Arrange
        var source = new object();

        // Act
        var origin = ChangeOrigin.Confirmed(source);

        // Assert
        Assert.Equal(ChangeOriginKind.Confirmed, origin.Kind);
        Assert.Same(source, origin.Source);
    }

    [Fact]
    public void WhenFactoryReceivesNullSource_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => ChangeOrigin.FromSource(null!));
        Assert.Throws<ArgumentNullException>(() => ChangeOrigin.Confirmed(null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ChangeOriginTests"`
Expected: compile error, `ChangeOrigin` not defined.

- [ ] **Step 3: Implement**

```csharp
namespace Namotion.Interceptor;

/// <summary>
/// The kind of origin of a property change. Byte-backed deliberately: inside
/// <c>SubjectPropertyChange</c> the runtime can fold a byte into padding where a wider
/// enum could grow the struct. Do not widen.
/// </summary>
public enum ChangeOriginKind : byte
{
    Local = 0,
    FromSource = 1,
    Confirmed = 2,
}

/// <summary>
/// Typed provenance of a property change. A change carries a source only when its stored
/// value is exactly the value that source sent (<see cref="ChangeOriginKind.FromSource"/>)
/// or confirmed (<see cref="ChangeOriginKind.Confirmed"/>). Everything else is
/// <see cref="ChangeOriginKind.Local"/>. The default value is Local.
/// </summary>
public readonly struct ChangeOrigin
{
    public ChangeOriginKind Kind { get; }

    /// <summary>Non-null exactly when <see cref="Kind"/> is not Local.</summary>
    public object? Source { get; }

    private ChangeOrigin(ChangeOriginKind kind, object? source)
    {
        Kind = kind;
        Source = source;
    }

    public static ChangeOrigin Local => default;

    public static ChangeOrigin FromSource(object source) =>
        new(ChangeOriginKind.FromSource, source ?? throw new ArgumentNullException(nameof(source)));

    public static ChangeOrigin Confirmed(object source) =>
        new(ChangeOriginKind.Confirmed, source ?? throw new ArgumentNullException(nameof(source)));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ChangeOriginTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/ChangeOrigin.cs src/Namotion.Interceptor.Tests/ChangeOriginTests.cs
git commit -m "feat: add ChangeOrigin and ChangeOriginKind core types (#345)"
```

### Task 2: PendingOrigin one-shot stamp

**Files:**
- Create: `src/Namotion.Interceptor/PendingOrigin.cs`
- Test: `src/Namotion.Interceptor.Tests/PendingOriginTests.cs`

**Interfaces:**
- Consumes: `ChangeOrigin` (Task 1), `PropertyReference` (existing). Target matching MUST use `PropertyReference.Equals` (via its `Comparer`), which is subject reference equality plus an ordinal `Name` comparison (one reference compare plus one ordinal string compare), not a bare `ReferenceEquals` on the name: inbound applies carry non-interned property names deserialized from JSON, so a literal reference comparison on `Name` would never match and echo suppression would silently break.
- Produces: `public static class PendingOrigin` with `public static PendingOriginScope Arm(PropertyReference target, ChangeOrigin origin, object? sentValue)` and `internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue)`; `public readonly ref struct PendingOriginScope : IDisposable` whose `Dispose` clears the slot unconditionally.

- [ ] **Step 1: Write the failing tests**

Use any `[InterceptorSubject]` test model already in `Namotion.Interceptor.Tests` to build a `PropertyReference` (for example `new PropertyReference(subject, "Name")`; follow the pattern used by existing tests in that project).

```csharp
namespace Namotion.Interceptor.Tests;

public class PendingOriginTests
{
    private static PropertyReference CreateProperty(string name = "Name")
    {
        var subject = new Person(InterceptorSubjectContext.Create());
        return new PropertyReference(subject, name);
    }

    [Fact]
    public void WhenArmedAndConsumedWithMatchingProperty_ThenOriginAndSentValueAreReturned()
    {
        // Arrange
        var property = CreateProperty();
        var source = new object();

        // Act & Assert
        using (PendingOrigin.Arm(property, ChangeOrigin.FromSource(source), "sent"))
        {
            Assert.True(PendingOrigin.TryConsume(property, out var origin, out var sentValue));
            Assert.Equal(ChangeOriginKind.FromSource, origin.Kind);
            Assert.Same(source, origin.Source);
            Assert.Equal("sent", sentValue);
        }
    }

    [Fact]
    public void WhenConsumedTwice_ThenSecondConsumeReturnsLocal()
    {
        // Arrange
        var property = CreateProperty();
        using (PendingOrigin.Arm(property, ChangeOrigin.FromSource(new object()), null))
        {
            PendingOrigin.TryConsume(property, out _, out _);

            // Act
            var consumed = PendingOrigin.TryConsume(property, out var origin, out _);

            // Assert
            Assert.False(consumed);
            Assert.Equal(ChangeOriginKind.Local, origin.Kind);
        }
    }

    [Fact]
    public void WhenTargetDoesNotMatch_ThenConsumeReturnsLocalAndSlotStaysArmed()
    {
        // Arrange
        var armedProperty = CreateProperty("Name");
        var otherProperty = CreateProperty("OtherName");

        using (PendingOrigin.Arm(armedProperty, ChangeOrigin.FromSource(new object()), null))
        {
            // Act
            var mismatch = PendingOrigin.TryConsume(otherProperty, out var mismatchOrigin, out _);
            var match = PendingOrigin.TryConsume(armedProperty, out var matchOrigin, out _);

            // Assert
            Assert.False(mismatch);
            Assert.Equal(ChangeOriginKind.Local, mismatchOrigin.Kind);
            Assert.True(match);
            Assert.Equal(ChangeOriginKind.FromSource, matchOrigin.Kind);
        }
    }

    [Fact]
    public void WhenScopeIsDisposedWithoutConsumption_ThenSlotIsCleared()
    {
        // Arrange
        var property = CreateProperty();
        using (PendingOrigin.Arm(property, ChangeOrigin.FromSource(new object()), null))
        {
        }

        // Act
        var consumed = PendingOrigin.TryConsume(property, out var origin, out _);

        // Assert
        Assert.False(consumed);
        Assert.Equal(ChangeOriginKind.Local, origin.Kind);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PendingOriginTests"`
Expected: compile error, `PendingOrigin` not defined.

- [ ] **Step 3: Implement**

```csharp
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// One-shot, per-write origin handoff. <see cref="Arm"/> stores a pending stamp for exactly
/// one write of one property; the matching write chain consumes it at
/// <c>PropertyWriteContext</c> construction. Nested writes (hooks, INPC handlers, derived
/// recalculations) never inherit it: the slot is either already consumed or targets a
/// different property. The scope captures the previous frame and restores it on dispose
/// (a zero-allocation stack through nested ref structs, like SubjectChangeContextScope),
/// so a cancelled write cannot leak the stamp and a nested stamped write cannot destroy
/// an outer stamp. Same-property re-entry from OnChanging is unsupported (the inner
/// invocation consumes the stamp). Thread-static by design: arm and consume happen
/// synchronously within one call frame, never across await. Internal: producers use
/// intent-level APIs (SetValueFromSource, ApplySubjectUpdate, transaction replay).
/// </summary>
internal static class PendingOrigin
{
    [ThreadStatic] private static bool _armed;
    [ThreadStatic] private static PropertyReference _target;
    [ThreadStatic] private static ChangeOrigin _origin;
    [ThreadStatic] private static object? _sentValue;

    internal static PendingOriginScope Arm(PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        var scope = new PendingOriginScope(_armed, _target, _origin, _sentValue);
        _armed = true;
        _target = target;
        _origin = origin;
        _sentValue = sentValue;
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue)
    {
        if (_armed && _target.Equals(property))
        {
            origin = _origin;
            sentValue = _sentValue;
            _armed = false;
            _target = default;
            _origin = default;
            _sentValue = null;
            return true;
        }

        origin = ChangeOrigin.Local;
        sentValue = null;
        return false;
    }

    internal static void Restore(bool armed, in PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        _armed = armed;
        _target = target;
        _origin = origin;
        _sentValue = sentValue;
    }
}

internal readonly ref struct PendingOriginScope
{
    private readonly bool _previousArmed;
    private readonly PropertyReference _previousTarget;
    private readonly ChangeOrigin _previousOrigin;
    private readonly object? _previousSentValue;

    internal PendingOriginScope(bool armed, PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        _previousArmed = armed;
        _previousTarget = target;
        _previousOrigin = origin;
        _previousSentValue = sentValue;
    }

    public void Dispose() => PendingOrigin.Restore(_previousArmed, in _previousTarget, _previousOrigin, _previousSentValue);
}
```

`PendingOrigin` is internal; the test project reaches it via the existing `InternalsVisibleTo("Namotion.Interceptor.Tests")`. Add a fifth test for the restore semantics:

```csharp
[Fact]
public void WhenNestedArmScopeIsDisposed_ThenOuterStampIsRestored()
{
    // Arrange
    var outerProperty = CreateProperty("Name");
    var innerProperty = CreateProperty("OtherName");
    var outerSource = new object();

    using (PendingOrigin.Arm(outerProperty, ChangeOrigin.FromSource(outerSource), "outer"))
    {
        using (PendingOrigin.Arm(innerProperty, ChangeOrigin.FromSource(new object()), "inner"))
        {
            PendingOrigin.TryConsume(innerProperty, out _, out _);
        }

        // Act: after the inner scope disposes, the outer stamp must be intact.
        var consumed = PendingOrigin.TryConsume(outerProperty, out var origin, out var sentValue);

        // Assert
        Assert.True(consumed);
        Assert.Same(outerSource, origin.Source);
        Assert.Equal("outer", sentValue);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PendingOriginTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/PendingOrigin.cs src/Namotion.Interceptor.Tests/PendingOriginTests.cs
git commit -m "feat: add internal PendingOrigin one-shot stamp with target matching and frame restore (#345)"
```

### Task 3: Origin on PropertyWriteContext, consumption and finalization

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs` (both `PropertyWriteContext<TProperty>` constructors, new members)
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs` (both terminal delegates)
- Test: `src/Namotion.Interceptor.Tests/OriginWriteContextTests.cs`

**Interfaces:**
- Consumes: `PendingOrigin.TryConsume` (Task 2).
- Produces: `public ChangeOrigin Origin { get; internal set; }` and `internal object? SentValue` on `PropertyWriteContext<TProperty>`; `internal void FinalizeOrigin()` called by the terminal write delegate right after `IsWritten = true`.

- [ ] **Step 1: Write the failing tests**

The test writes through a real subject with a source armed, observing `SubjectPropertyChange` is not available yet (Tracking changes come in Task 5), so observe via a probe write interceptor registered in the context:

```csharp
namespace Namotion.Interceptor.Tests;

public class OriginWriteContextTests
{
    private sealed class OriginProbe : IWriteInterceptor
    {
        public ChangeOriginKind? KindBeforeWrite;
        public ChangeOriginKind? KindAfterWrite;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            KindBeforeWrite = context.Origin.Kind;
            next(ref context);
            KindAfterWrite = context.Origin.Kind;
        }
    }

    [Fact]
    public void WhenWriteIsArmedFromSource_ThenOriginIsFromSourceBeforeAndAfterWrite()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var person = new Person(context);
        var property = new PropertyReference(person, "Name");
        var source = new object();

        // Act
        using (PendingOrigin.Arm(property, ChangeOrigin.FromSource(source), "sent"))
        {
            person.Name = "sent";
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenStoredValueDiffersFromSentValue_ThenOriginIsFinalizedToLocal()
    {
        // Arrange: arm with sentValue "sent" but write a different value, as a
        // transforming hook or rewriting interceptor would.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var person = new Person(context);
        var property = new PropertyReference(person, "Name");

        // Act
        using (PendingOrigin.Arm(property, ChangeOrigin.FromSource(new object()), "sent"))
        {
            person.Name = "transformed";
        }

        // Assert: attempted origin visible mid-chain, Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenWriteIsNotArmed_ThenOriginIsLocal()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var person = new Person(context);

        // Act
        person.Name = "value";

        // Assert
        Assert.Equal(ChangeOriginKind.Local, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }
}
```

Adjust `AddService`/model names to the existing test-project idiom (check `Namotion.Interceptor.Tests` for how interceptors are registered, for example `context.WithService(() => probe)` or equivalent, and reuse an existing `Person` model).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OriginWriteContextTests"`
Expected: compile error, `context.Origin` not defined.

- [ ] **Step 3: Implement**

In `PropertyWriteContext<TProperty>` (`IWriteInterceptor.cs`), add:

```csharp
/// <summary>
/// The origin of this write. Before the terminal write executes this is the attempted
/// origin (what the arming caller declared); when the terminal write lands (the same
/// point <see cref="IsWritten"/> becomes true) it is finalized: a stamped origin whose
/// final value differs from the sent value becomes Local, because the stored value was
/// computed locally rather than taken from the source.
/// </summary>
public ChangeOrigin Origin { get; internal set; }

/// <summary>The value the source sent, valid when <see cref="Origin"/> is stamped.</summary>
internal object? SentValue { get; }
```

In BOTH constructors, after the existing assignments, consume the stamp:

```csharp
PendingOrigin.TryConsume(in property, out var origin, out var sentValue);
Origin = origin;
SentValue = sentValue;
```

Consuming the pending stamp at construction is a side effect: any direct construction of `PropertyWriteContext<TProperty>` (tests, benchmarks, not only the interceptor chain) drains the armed stamp. Both constructors MUST carry a doc comment stating this, so a caller who news up a context by hand knows it consumes the pending origin.

Add the finalization method to the struct:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal void FinalizeOrigin()
{
    if (Origin.Kind != ChangeOriginKind.Local && !Equals(SentValue, NewValue))
    {
        Origin = ChangeOrigin.Local;
    }
}
```

In `WriteInterceptorFactory.cs`, in BOTH terminal delegates, immediately after `context.IsWritten = true;` add:

```csharp
context.FinalizeOrigin();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OriginWriteContextTests"`
Expected: 3 passed. Also run the whole project: `dotnet test src/Namotion.Interceptor.Tests`.

- [ ] **Step 5: Commit**

```bash
git add -A src/Namotion.Interceptor src/Namotion.Interceptor.Tests
git commit -m "feat: consume pending origin into PropertyWriteContext and finalize at terminal write (#345)"
```

### Task 4: Timestamps-only SubjectChangeContext and arming SetValueFromSource

**Files:**
- Modify: `src/Namotion.Interceptor/SubjectChangeContext.cs` (remove `Source` field, `WithSource`, `WithState`; add `WithTimestamps`)
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectChangeContextExtensions.cs` (add public `SetValueFromOrigin` primitive that arms instead of scoping; `SetValueFromSource` forwards to it)
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs:357` (delete the `WithSource(null)` using, keep its body)
- Test: existing suites; update `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Produces: `public static SubjectChangeContextScope WithTimestamps(DateTimeOffset? changed, DateTimeOffset? received)`; `SubjectChangeContext` no longer has `Source`. New public Tracking primitive `SubjectChangeContextExtensions.SetValueFromOrigin(this PropertyReference property, ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? value)` that arms any origin kind; `SetValueFromSource` forwards to it.
- Consumes: `PendingOrigin.Arm` (Task 2).

Core's `InternalsVisibleTo` covers Tracking, the tests, and the benchmark project, but NOT `Namotion.Interceptor.Connectors`, so the Connectors update appliers (Task 6) cannot call the internal `PendingOrigin.Arm`. `SetValueFromOrigin` is their public intent-level entry point: it performs the write itself, so the raw slot stays internal.

- [ ] **Step 1: Remove source from the ambient context**

In `SubjectChangeContext.cs`: delete the `public readonly object? Source;` field, the `WithSource` method, and the `WithState` method. Update the private constructor to drop the source parameter. Add:

```csharp
/// <summary>
/// Enters a scope that sets the changed timestamp and, when <paramref name="received"/> is
/// non-null, the received timestamp. A null <paramref name="received"/> preserves the ambient
/// received timestamp (exactly as <see cref="WithChangedTimestamp"/> does), rather than
/// resetting it to the sentinel the way the deleted <c>WithState</c> did. This keeps the
/// inbound apply path behavior-identical to master's <c>WithChangedTimestamp</c> wrapping.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static SubjectChangeContextScope WithTimestamps(DateTimeOffset? changed, DateTimeOffset? received)
{
    var previousState = _current;
    _current = new SubjectChangeContext(
        changed?.UtcTicks ?? NullTimestampSentinel,
        received?.UtcTicks ?? previousState._receivedTimestamp);
    return new SubjectChangeContextScope(previousState);
}
```

Keep `WithChangedTimestamp`, `GetTimestampFunction`, `CaptureTimestamp`, `ReceivedTimestamp`, `CurrentChangedTimestamp`, `ResolveChangedTimestamp`, and the scope struct unchanged (minus the source in the constructor).

- [ ] **Step 2: Add SetValueFromOrigin and make SetValueFromSource forward to it**

Add a public `SetValueFromOrigin` primitive that arms any origin kind and performs the write, then make `SetValueFromSource` a thin forwarder. Connectors cannot reach the internal `PendingOrigin.Arm`, so this public Tracking extension is the appliers' entry point (see Task 6). Contract: when `receivedTimestamp` is null, `SetValueFromOrigin` preserves the ambient received timestamp (via the `WithTimestamps` null fallback from Step 1) rather than overwriting it with the null sentinel; only a non-null `receivedTimestamp` replaces the ambient value. This is what keeps the applier path (which passes null received) behavior-identical to master's `WithChangedTimestamp` wrapping:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static void SetValueFromOrigin(
    this PropertyReference property,
    ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
    object? value)
{
    using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
    using (PendingOrigin.Arm(property, origin, value))
    {
        property.Metadata.SetValue?.Invoke(property.Subject, value);
    }
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static void SetValueFromSource(
    this PropertyReference property,
    object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
    object? valueFromSource) =>
    property.SetValueFromOrigin(ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, valueFromSource);
```

- [ ] **Step 3: Delete the derived anti-scope**

In `DerivedPropertyChangeHandler.cs` around line 357, remove the `using (SubjectChangeContext.WithSource(null))` wrapper, keeping `SetPropertyValueWithInterception` and the `IRaisePropertyChanged` raise as direct statements.

- [ ] **Step 4: Build and fix all compile errors in core and Tracking only**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | head -50`
Expected: errors remain in Tracking (publishers, transactions) and downstream projects referencing `Source` and `WithState`. Those are Tasks 5 and 6; only fix errors in files this task owns. If the build cannot be partially green, proceed to Task 5 before running the full suite and fold the commit there (keep the commits separate if the solution builds).

- [ ] **Step 5: Commit (if building)**

```bash
git add -A src/Namotion.Interceptor src/Namotion.Interceptor.Tracking
git commit -m "feat: make SubjectChangeContext timestamps-only, arm origin in SetValueFromSource (#345)"
```

### Task 5: SubjectPropertyChange.Origin, publishers, transactions, echo skip

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs` (`Source` to `Origin`, `Create` signature, `WithSource` to `WithOrigin`)
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs:65-73`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs:30-38`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs:72-79`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectPropertyChangeOperations.cs:116-127` and the capture/queue paths that pass `Source`
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:145`
- Modify: `src/Namotion.Interceptor.Connectors/Transactions/SourceTransactionWriter.cs:306,325,394` and its `TryGetSource` grouping input (reads `change.Origin.Source`)
- Test: compile-driven plus existing transaction and connector suites

**Interfaces:**
- Produces: `SubjectPropertyChange.Origin` (`ChangeOrigin`), `SubjectPropertyChange.Create(property, ChangeOrigin origin, changedTimestamp, receivedTimestamp, oldValue, newValue)`, `WithOrigin(ChangeOrigin origin)`.

- [ ] **Step 1: Swap the change struct**

In `SubjectPropertyChange.cs`: replace `public object? Source { get; }` with FLATTENED storage. The CLR does not fold a nested struct into container padding, so do not store a `ChangeOrigin` field; store the kind byte (folds into padding) and the source reference (reuses the old `Source` slot) separately and recompose:

```csharp
private readonly ChangeOriginKind _originKind;
private readonly object? _originSource;

public ChangeOrigin Origin => ChangeOrigin.FromParts(_originKind, _originSource);
```

Add `internal static ChangeOrigin FromParts(ChangeOriginKind kind, object? source)` to `ChangeOrigin` (internal, no validation; Tracking has `InternalsVisibleTo`). Thread `ChangeOrigin origin` through the private constructor and `Create` (replacing `object? source`, storing `origin.Kind` and `origin.Source`), and replace the `WithSource(object? source)` with-er with:

```csharp
public SubjectPropertyChange WithOrigin(ChangeOrigin origin) =>
    new(Property, origin, ChangedTimestamp, ReceivedTimestamp, _oldValueStorage, _newValueStorage, _oldBoxedHolder, _newBoxedHolder);
```

Also fix `MergeWithNewer`: it currently copies only `newerChange.Source` into the merged change, so under the flattened layout a deduplicated change silently loses its kind and reverts to `Local`. Copy the full flattened origin (both `newerChange._originKind` and `newerChange._originSource`, or `newerChange.Origin` through the private constructor):

```csharp
public SubjectPropertyChange MergeWithNewer(SubjectPropertyChange newerChange) =>
    new(Property, newerChange.Origin, newerChange.ChangedTimestamp, newerChange.ReceivedTimestamp,
        _oldValueStorage, newerChange._newValueStorage, _oldBoxedHolder, newerChange._newBoxedHolder);
```

Add a merge-origin test in the Tracking test project: merging a `FromSource(S)` change with a newer `FromSource(S)` change leaves the result `Origin.Kind == FromSource` and `Origin.Source == S` (both kind and source survive the merge).

Add a size-regression test in the Tracking test project:

```csharp
[Fact]
public void WhenMeasuringSubjectPropertyChange_ThenSizeDidNotGrowVersusSourceField()
{
    // Assert: master's struct size with the 8-byte Source reference. If this fails,
    // the flattened layout regressed; investigate before accepting a larger struct.
    Assert.True(Unsafe.SizeOf<SubjectPropertyChange>() <= 96,
        $"SubjectPropertyChange grew to {Unsafe.SizeOf<SubjectPropertyChange>()} bytes");
}
```

First measure the actual master size with a throwaway `Unsafe.SizeOf` print on master and put that number into the assertion instead of 96.

- [ ] **Step 2: Publishers read the context**

In both `PropertyChangeQueue.WriteProperty` and `PropertyChangeObservable.WriteProperty`, the block after `next(ref context)` currently reads:

```csharp
var changeContext = SubjectChangeContext.Current;
var propertyChange = SubjectPropertyChange.Create(
    context.Property,
    changeContext.Source,
    context.WriteTimestampForPublishing,
    changeContext.ReceivedTimestamp,
    oldValue,
    newValue);
```

Replace with:

```csharp
var propertyChange = SubjectPropertyChange.Create(
    context.Property,
    context.Origin,
    context.WriteTimestampForPublishing,
    SubjectChangeContext.Current.ReceivedTimestamp,
    oldValue,
    newValue);
```

- [ ] **Step 3: Transactions**

In `SubjectTransactionInterceptor.WriteProperty`, replace `currentContext.Source` in the `CaptureChange` call with `context.Origin` and adjust `CaptureChange`/`SubjectTransaction` storage to hold `ChangeOrigin` (follow the compiler; the value flows into `SubjectPropertyChange.Create` inside the transaction, which now takes `ChangeOrigin`). The ambient `currentContext` read stays only for `ReceivedTimestamp`.

In `SubjectPropertyChangeOperations.TryApplyLocalChange`, replace the `WithState` wrap:

```csharp
using (SubjectChangeContext.WithTimestamps(change.ChangedTimestamp, change.ReceivedTimestamp))
using (PendingOrigin.Arm(change.Property, change.Origin, change.GetNewValue<object?>()))
{
    metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
}
```

Arming with a `Local` origin is a no-op stamp (consume yields Local), so revert paths re-apply each change with its original provenance without branching.

- [ ] **Step 4: Echo skip and commit stamping**

In `ChangeQueueProcessor.cs:145`, replace `if (change.Source == _source)` with `if (ReferenceEquals(change.Origin.Source, _source))`.

In `SourceTransactionWriter.cs`, replace all three `snapshot[slot].WithSource(source)` calls with `snapshot[slot].WithOrigin(ChangeOrigin.Confirmed(source))`.

- [ ] **Step 5: Build the whole solution and fix remaining fallout compile-driven**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | grep -E "error" | head -30`

Remaining known consumers of the old `Source` member (fix each to `Origin.Source` or `Origin.Kind` as semantically appropriate, keeping behavior identical): search with `grep -rn "\.Source" src --include="*.cs" | grep -i "change\." `. Any test code updates belong to Task 7.

- [ ] **Step 6: Run Tracking and Connectors tests**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests && dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: failures only in tests that assert on `change.Source` (fixed in Task 7). Everything else passes.

- [ ] **Step 7: Commit**

```bash
git add -A src
git commit -m "feat: publish typed ChangeOrigin on SubjectPropertyChange, stamp Confirmed on commit (#345)"
```

### Task 6: ApplySubjectUpdate source parameter and WebSocket migration

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateExtensions.cs:18-24` (required `ChangeOrigin origin` parameter after `subjectFactory`)
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` and `SubjectItemsUpdateApplier.cs` (thread `origin` to every property write site; each site currently wrapped in `WithChangedTimestamp` applies via `SetValue` or `RegisteredSubjectProperty`; when `origin.Kind != ChangeOriginKind.Local`, apply through `SetValueFromOrigin(origin, propertyUpdate.Timestamp, null, value)`, the public Tracking primitive from Task 4 that arms `FromSource` and `Confirmed` alike, since Connectors cannot reach the internal `PendingOrigin.Arm`; for `Local`, keep the current unarmed path, since `Local` is the default and needs no stamp). The five sites (`SubjectUpdateApplier.cs:84,131,139` and `SubjectItemsUpdateApplier.cs:124,209`) are each wrapped in `WithChangedTimestamp(propertyUpdate.Timestamp)` today; `propertyUpdate.Timestamp` MUST be passed as the `changedTimestamp` argument of `SetValueFromOrigin` at every site, or the inbound changed-timestamp is silently replaced with capture-time `UtcNow` and provenance is corrupted
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs:200-207` (pass `ChangeOrigin.FromSource(connection)`), `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs:296-299,540-543` (pass `ChangeOrigin.FromSource(this)` / `ChangeOrigin.FromSource(state.source)`); drop the `WithSource` usings; correct the lock comment at `WebSocketSubjectHandler.cs:200` to say the lock serializes update application, not that thread-static state requires it
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/Verification/FailureDiagnostics.cs:175` (pass `ChangeOrigin.Local`)
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs:127` (extend `CreatePropertyChangeQueueSubscription`'s signature with an optional `Func<SubjectPropertyChange, bool>? filter = null` after the existing `scheduler` parameter, and thread it into `PropertyChangeQueue.Subscribe` and the per-subscription enqueue path) and `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs` / the `PropertyChangeQueueSubscription` (accept the optional filter and apply it at ENQUEUE time, so a change the filter rejects never enters that subscription's queue)
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` (pass an own-source filter predicate into `CreatePropertyChangeQueueSubscription` so the own-source skip `ReferenceEquals(change.Origin.Source, _source)` runs at enqueue time; the dequeue-time skip in `ProcessAsync` stays as a cheap defensive check)
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs` (reorder `ExecuteAsync` around lines 76-101: construct the `ChangeQueueProcessor` and its subscription BEFORE `LoadInitialStateAndResumeAsync`, then start `ProcessAsync` after `ReapplyRetryQueue`). With the subscription live before the load and the enqueue filter dropping own-source echoes, the initial-load flood never grows the buffer, and any locally computed write-back produced during the load is captured and delivered once processing starts
- Modify: `docs/connectors.md` (document the lifecycle ordering fix): `SubjectSourceBase` creates its `ChangeQueueProcessor` subscription before the initial load and applies the processor's own-source skip at enqueue time, so a locally computed write-back produced during initial load (for example a hook transforming an inbound initial value) is delivered to the source once processing starts, while the initial snapshot's own-source echoes are filtered at enqueue and never grow the buffer
- Test: `src/Namotion.Interceptor.Connectors.Tests` update-apply suites gain a source overload test and a timestamp-preservation test (an applied update's published change carries `propertyUpdate.Timestamp`, not capture-time `UtcNow`)

**Interfaces:**
- Produces: `ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectFactory? subjectFactory, ChangeOrigin origin, Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)`.
- Produces: `CreatePropertyChangeQueueSubscription(this IInterceptorSubjectContext context, IScheduler? scheduler = null, Func<SubjectPropertyChange, bool>? filter = null)`; the optional `filter` runs at enqueue time (a rejected change never enters that subscription's queue). `ChangeQueueProcessor` passes `change => !ReferenceEquals(change.Origin.Source, _source)` so own-source echoes are filtered at enqueue.

- [ ] **Step 1: Write the failing test** (in the existing update-extensions test class in `Namotion.Interceptor.Connectors.Tests/Updates`, following its Arrange idiom)

```csharp
[Fact]
public void WhenUpdateIsAppliedWithSource_ThenChangeCarriesFromSourceOrigin()
{
    // Arrange: subject with full tracking, subscribe to the change queue,
    // build a SubjectUpdate for one property (reuse the idiom of existing
    // SubjectUpdateExtensionsTests).
    var source = new object();

    // Act
    subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(source));

    // Assert: the published change has Origin.Kind == FromSource and Origin.Source == source.
}
```

Fill Arrange/Assert with the exact helpers used by neighboring tests in that file; the assertion target is `SubjectPropertyChange.Origin`.

- [ ] **Step 2: Run it, expect compile failure** (no `source` parameter yet).

- [ ] **Step 3: Implement the parameter and threading**

Add the required `ChangeOrigin origin` parameter, thread it through `SubjectUpdateApplier.ApplyUpdate` and `SubjectItemsUpdateApplier` down to the write sites, and route stamped (`FromSource`/`Confirmed`) writes through `SetValueFromOrigin`; keep the current unarmed path for `Local`. Update the three WebSocket sites and the ConnectorTester site. Also update the code sample in the XML doc comment of `ApplySubjectUpdate` if present.

- [ ] **Step 4: Enqueue-time own-source filter**

Extend `CreatePropertyChangeQueueSubscription` (`InterceptorSubjectContextExtensions.cs:127`) with an optional `Func<SubjectPropertyChange, bool>? filter = null` after `scheduler`, thread it into `PropertyChangeQueue.Subscribe` and the per-subscription enqueue path, and apply it at ENQUEUE time so a change the filter rejects never enters that subscription's queue. In `ChangeQueueProcessor`, pass `change => !ReferenceEquals(change.Origin.Source, _source)` into the subscription so own-source echoes are filtered at enqueue. Keep the dequeue-time skip in `ProcessAsync` (`ChangeQueueProcessor.cs:145`) as a cheap defensive check; the two use the same predicate, so delivery stays behavior-identical.

- [ ] **Step 5: Reorder SubjectSourceBase lifecycle**

Reorder `SubjectSourceBase.ExecuteAsync` (around lines 76-101): construct the `ChangeQueueProcessor` (and therefore its queue subscription) BEFORE `LoadInitialStateAndResumeAsync`, then call `ReapplyRetryQueue`, then start `ProcessAsync`. With the subscription live before the load, a locally computed write-back produced during initial load is captured and delivered once processing starts; because the enqueue filter drops own-source echoes, the initial snapshot flood never grows the buffer. Update `docs/connectors.md` (near the change-source semantics the docs task rewrites) to document this lifecycle ordering fix, not a limitation: the subscription exists before the initial load and own-source echoes are filtered at enqueue, so an initial-load write-back reaches the source once processing starts while the snapshot flood stays out of the buffer.

- [ ] **Step 6: Lifecycle ordering tests**

In `Namotion.Interceptor.Connectors.Tests`, following the existing `SubjectSourceBase` / `ChangeQueueProcessor` test idiom:
- A hook-transformed inbound write-back applied during initial load is delivered to the source once `ProcessAsync` starts (with the subscription created before the load).
- A large simulated initial snapshot (many `SetValueFromSource(this)` applies) leaves the subscription buffer empty: all own-source echoes are filtered at enqueue, so the bounded-memory property holds.
- Steady-state echo suppression is unchanged: an own-source change is still not written back to its source, and other-source and local changes still are.

- [ ] **Step 7: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests && dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: all pass, including WebSocket echo-suppression integration tests (they prove end-to-end equivalence of the new stamping).

- [ ] **Step 8: Commit**

```bash
git add -A src docs
git commit -m "feat: require typed ChangeOrigin on ApplySubjectUpdate, migrate WebSocket applies (#345)"
```

### Task 7: Validator provenance and test-suite fallout

**Files:**
- Create: `src/Namotion.Interceptor.Validation/PropertyValidationContext.cs`
- Modify: `src/Namotion.Interceptor.Validation/IPropertyValidator.cs`, `ValidationInterceptor.cs:24`, `DataAnnotationsValidator.cs`
- Modify: `src/Namotion.Interceptor.AspNetCore/SubjectAspNetCoreServiceCollection.cs` (implementor)
- Modify: all test files that implement `IPropertyValidator` or assert `change.Source`
- Test: `src/Namotion.Interceptor.Validation.Tests` (or the suite hosting validation tests) gains an origin-aware validator test

**Interfaces:**
- Produces: `public readonly struct PropertyValidationContext<TProperty>(PropertyReference property, TProperty value, ChangeOrigin origin)` with `Property`, `Value`, `Origin` properties; `IPropertyValidator.Validate<TProperty>(in PropertyValidationContext<TProperty> context)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void WhenValueComesFromSource_ThenProvenanceAwareValidatorCanSkipStrictValidation()
{
    // Arrange: a validator that rejects "invalid" only when context.Origin.Kind == ChangeOriginKind.Local,
    // registered on a context with validation; a subject property.
    // Act: write "invalid" locally (throws ValidationException) and via
    // property.SetValueFromSource(source, null, null, "invalid") (does not throw).
    // Assert both outcomes.
}
```

Write it out fully against the existing validation test idiom.

- [ ] **Step 2: Run it, expect compile failure.**

- [ ] **Step 3: Implement**

```csharp
namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validation input for a single property write. Origin is the attempted origin of the
/// write (validation runs before the terminal write): Local for user writes, FromSource
/// for inbound source applies, Confirmed for transaction commit replays.
/// </summary>
public readonly struct PropertyValidationContext<TProperty>(
    PropertyReference property, TProperty value, ChangeOrigin origin)
{
    public PropertyReference Property { get; } = property;
    public TProperty Value { get; } = value;
    public ChangeOrigin Origin { get; } = origin;
}
```

`IPropertyValidator`:

```csharp
IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context);
```

`ValidationInterceptor.cs:24` builds it: `validator.Validate(new PropertyValidationContext<TProperty>(context.Property, context.NewValue, context.Origin))`. `DataAnnotationsValidator` updates its signature and keeps ignoring the origin.

- [ ] **Step 4: Fix all remaining test fallout solution-wide**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | grep error | head -30`, fix every remaining `Source`/`WithState`/validator-signature reference in test projects (assertions move from `change.Source` to `change.Origin.Source` and, where the test checks locality, `change.Origin.Kind`).

- [ ] **Step 5: Full unit test run**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A src
git commit -m "feat: flow ChangeOrigin into PropertyValidationContext for provenance-aware validation (#345)"
```

### Task 8: Port PR #348 behavioral tests as the semantic safety net

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectChangingHookTransformTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyLocalOriginTests.cs`
- Create: model files those tests need (`ClampingDevice`, `CascadingDevice` changes)

**Interfaces:**
- Consumes: everything from Tasks 1 to 7.

- [ ] **Step 1: Extract the tests from the abandoned branch**

```bash
git show feature/hook-cascade-null-source-design:src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs > src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs
git show feature/hook-cascade-null-source-design:src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectChangingHookTransformTests.cs > src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectChangingHookTransformTests.cs
git show feature/hook-cascade-null-source-design:src/Namotion.Interceptor.Connectors.Tests/Models/ClampingDevice.cs > src/Namotion.Interceptor.Connectors.Tests/Models/ClampingDevice.cs
git show feature/hook-cascade-null-source-design:src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyLocalOriginTests.cs > src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyLocalOriginTests.cs
```

Also diff `TransactionTestBase.cs` and `CascadingDevice.cs` on that branch (`git diff master feature/hook-cascade-null-source-design -- <path>`) and port helper additions the tests rely on.

- [ ] **Step 2: Adapt assertions to the new API**

Replace every `change.Source` assertion with `change.Origin` equivalents: `Assert.Null(change.Source)` becomes `Assert.Equal(ChangeOriginKind.Local, change.Origin.Kind)`; `Assert.Same(source, change.Source)` becomes `Assert.Same(source, change.Origin.Source)`. Drop any test that pins generated-code shape (hook scope emission), since PR 1 does not touch the generator.

- [ ] **Step 3: Run and make them pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests && dotnet test src/Namotion.Interceptor.Tracking.Tests`
Expected: all pass without production-code changes. A failure here means the mechanism does not reproduce PR #348 semantics; stop and analyze against the spec's scenario table before touching anything.

- [ ] **Step 4: Commit**

```bash
git add -A src
git commit -m "test: port hook cascade and transform local-origin behavioral pins (#345)"
```

### Task 9: Public API snapshots and documentation (PR 1)

**Files:**
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` (plus any other failing PublicApi snapshots, for example Tracking or Validation test projects)
- Modify: `docs/connectors.md` (section "Change notification source semantics")
- Modify: `docs/tracking.md` (Change Sources section, line ~495)
- Modify: `docs/connectors-subject-updates.md` (Applying Updates section, line ~100)
- Modify: `docs/connectors-opcua-client.md` (feedback-loop section, line ~715)
- Modify: `docs/design/tracking-derived-properties.md` (lines ~320-345, the `WithSource(null)` pseudocode and bullet)

- [ ] **Step 1: Accept intended API snapshot changes**

Run `dotnet test src/Namotion.Interceptor.slnx --filter "FullyQualifiedName~PublicApi"`; for each failure, inspect the diff (must show exactly: `ChangeOrigin`, `ChangeOriginKind`, `PendingOrigin`, `PropertyWriteContext.Origin`, `WithTimestamps` added; `Source`, `WithSource`, `WithState`, old validator signature removed), then copy `.received.txt` over `.verified.txt`.

- [ ] **Step 2: Rewrite the docs**

`docs/connectors.md`, replace the "Change notification source semantics" section body with:

```markdown
The `Origin` of a change notification is typed (`ChangeOrigin`): `FromSource` when an
inbound update stored exactly the value the source sent, `Confirmed` when a source
transaction commit stored the value the source acknowledged, and `Local` for everything
else. A change carries a source only when its stored value is exactly the value that
source sent or confirmed. The outbound change queue skips changes whose origin source is
the target source itself, so a committed value is written to its source exactly once, by
the commit.

Origin is stamped per write at the apply call (`SetValueFromSource`,
`ApplySubjectUpdate` with a `FromSource` origin, transaction commit replay). Nothing inherits it:
hook cascades, `INotifyPropertyChanged` handler write-backs, derived property
recalculations, and lifecycle handler writes are all `Local` and therefore flow to
bound sources like any local write. When an `OnChanging` hook or a write interceptor
changes the incoming value during a stamped write, the stored value no longer equals
the sent value and the write publishes as `Local`, so corrections flow back to the
source. Transforms must be projections (idempotent, like clamping); reference-typed
values must be reassigned, not mutated in place, to be detected.

Provenance-aware validators receive the origin via `PropertyValidationContext` and can
treat source values as authoritative while strictly validating local input.
```

`docs/tracking.md` (~495): replace the `WithSource` example with `property.SetValueFromSource(source, timestamp, receivedTimestamp, value)` and one sentence: source marking is per write; a scope-based source no longer exists.

`docs/connectors-subject-updates.md` (~100): replace the `using (SubjectChangeContext.WithSource(source))` example with:

```csharp
// Apply update from an external source (echo prevention via typed origin)
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(source));

// Apply update as a local mutation
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
```

`docs/connectors-opcua-client.md` (~715): same replacement pattern using `SetValueFromSource`.

`docs/design/tracking-derived-properties.md`: replace `WithSource(null):` pseudocode line with a note that derived recalculation writes are `Local` by default (no scope needed) and update the `WithSource(null)` bullet accordingly.

- [ ] **Step 3: Full test run, then commit**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
git add -A src docs
git commit -m "docs: describe per-write origin stamping and typed ChangeOrigin (#345)"
```

### Task 10: Benchmarks and PR 1 creation

- [ ] **Step 1: Run write-path benchmarks**

Run: `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*Write*"` (check the project's README or `--list flat` for the exact write benchmark names). Compare against master numbers (run the same filter on a master checkout if no baseline exists). Regressions above noise on the plain local write path are a stop-and-analyze signal.

- [ ] **Step 2: Push and create PR 1**

```bash
git push -u origin feature/change-origin-design
gh pr create --base master --title "Typed ChangeOrigin with one-shot source stamping" --body "..."
```

PR body: summary of the mechanism swap referencing `docs/superpowers/specs/2026-07-12-change-origin-design.md`, the scenario list 1 to 7, breaking changes list, benchmark results table, and `Closes #345`. No AI attribution. Also close PR #348 with a comment: `gh pr close 348 --comment "Superseded by the change-origin design (see docs/superpowers/specs/2026-07-12-change-origin-design.md); the mechanism moved from scope-based anti-scopes to per-write origin stamping."`

---

# Phase 2 (PR 2): Correction kind

### Task 11: Branch, Correction kind, detector

**Files:**
- Branch: `git checkout -b feature/change-origin-corrections feature/change-origin-design`
- Modify: `src/Namotion.Interceptor/ChangeOrigin.cs` (add `Correction = 3` and factory)
- Create: `src/Namotion.Interceptor.Tracking/Change/SourceCorrectionDetector.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs` (internal `EnqueueCorrection`)
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs` (register the detector with the queue registration)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/SourceCorrectionTests.cs`

**Interfaces:**
- Produces: `ChangeOriginKind.Correction`, `ChangeOrigin.Correction(object source)`, `SourceCorrectionDetector : IWriteInterceptor` with `[RunsFirst]` and `[RunsBefore(typeof(PropertyValueEqualityCheckHandler))]`.

**Ordering constraint (why this design):** `PropertyValueEqualityCheckHandler` is `[RunsFirst]` and suppresses unchanged writes by not calling `next`, so the queue interceptor never runs for them. Detection therefore lives in a dedicated interceptor ordered before the equality check.

- [ ] **Step 1: Write the failing tests** (the three-outcome matrix from the spec)

```csharp
namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SourceCorrectionTests
{
    // Model: an [InterceptorSubject] with an OnValueChanging hook clamping to 0..100
    // (reuse or mirror the ClampingDevice model from the Connectors tests).

    [Fact]
    public void WhenStoredValueChanges_ThenNormalChangeIsPublished()
    {
        // Arrange: model at 50, queue subscription.
        // Act: SetValueFromSource(source, null, null, 105); hook clamps to 100.
        // Assert: exactly one change, Origin.Kind == Local (transform), new value 100. No correction.
    }

    [Fact]
    public void WhenProjectedValueEqualsStoredValue_ThenCorrectionIsPublished()
    {
        // Arrange: model at 100, queue subscription.
        // Act: SetValueFromSource(source, null, null, 105); hook clamps to 100; equality suppresses.
        // Assert: exactly one queued change with Origin.Kind == Correction,
        //         Origin.Source == source, old == new == 100.
        //         The model value is still 100 and no PropertyChanged was raised.
        //         The correction's ChangedTimestamp is fresh (not the inbound scope time)
        //         and equals the property's write-timestamp metadata after the call.
    }

    [Fact]
    public void WhenSentValueEqualsStoredValue_ThenNothingIsPublished()
    {
        // Arrange: model at 100.
        // Act: SetValueFromSource(source, null, null, 100).
        // Assert: no change and no correction in the queue.
    }

    [Fact]
    public void WhenConfirmedWriteIsSuppressed_ThenNoCorrectionIsPublished()
    {
        // Arrange: model at 100.
        // Act: arm ChangeOrigin.Confirmed(source) for the property with sent value 105
        //      via PendingOrigin.Arm and invoke the setter with a value projecting to 100.
        // Assert: no correction (commit protocol already guarantees the source state).
    }

    [Fact]
    public void WhenInboundStampedWriteIsCapturedByTransaction_ThenNoCorrectionIsPublished()
    {
        // Arrange: model at 50, an active SubjectTransaction on the context.
        // Act: SetValueFromSource(source, null, null, 80); the transaction interceptor
        //      captures the value and stops the chain (IsWritten stays false).
        // Assert: no correction in the queue; the value is pending in the transaction.
        //         This pins that IsWritten == false alone never triggers synthesis.
    }

    [Fact]
    public void WhenReadInterceptorTransformsValue_ThenCorrectionCarriesObservableValue()
    {
        // Arrange: model at 100, a custom IReadInterceptor that returns a transformed
        //          value from the getter (for example appends a suffix or offsets by 1).
        // Act: SetValueFromSource(source, null, null, 105); hook clamps to 100; suppressed.
        // Assert: the correction carries the read-interceptor-observed value, pinning
        //         that corrections assert the observable value, not the backing field.
    }

    [Fact]
    public void WhenConcurrentWriteRacesTheSuppressedApply_ThenCorrectionIsNeverStale()
    {
        // Arrange: model at 100; a changing hook that clamps and, via an event/gate,
        //          allows a second thread to write 90 between the equality decision and
        //          the detector's synthesis (use ManualResetEventSlim inside the hook).
        // Act: thread 1 applies 105 from the source (projects to 100, suppressed);
        //      thread 2 writes 90 locally while thread 1 is parked in the hook.
        // Assert: drop-or-fresh, never stale. The correction is either absent (dropped
        //         because the write-timestamp moved under the lock) or carries the fresh
        //         post-write value (90); it never carries the stale 100.
    }
}
```

Write the bodies fully against the tracking test idioms (queue subscription via `context.CreatePropertyChangeQueueSubscription()`).

- [ ] **Step 2: Run, expect failures/compile errors.**

- [ ] **Step 3: Implement**

`ChangeOrigin.cs`: add `Correction = 3` to the enum and

```csharp
public static ChangeOrigin Correction(object source) =>
    new(ChangeOriginKind.Correction, source ?? throw new ArgumentNullException(nameof(source)));
```

`PropertyChangeQueue.cs`: add

```csharp
internal void EnqueueCorrection(in SubjectPropertyChange change) => Enqueue(in change);
```

`SourceCorrectionDetector.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Detects the diverged-source case: an inbound value whose projection equals the already
/// stored value is suppressed by the equality check, so no change publishes and the source
/// keeps its out-of-range value. This interceptor runs outside the equality check and,
/// when a FromSource write ends unwritten with a sent value that differs from the stored
/// value, synthesizes a Correction change (old == new == stored value) into the outbound
/// queue only. No model write occurred, so no hooks, INPC, or timestamps fire.
/// </summary>
[RunsFirst]
[RunsBefore(typeof(PropertyValueEqualityCheckHandler))]
public class SourceCorrectionDetector : IWriteInterceptor
{
    private readonly PropertyChangeQueue _queue;

    public SourceCorrectionDetector(PropertyChangeQueue queue)
    {
        _queue = queue;
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Capture the property's raw write-timestamp BEFORE the chain runs. A suppressed
        // write never updates it (the equality check stops before the terminal write),
        // so under the lock any movement means a real competing write landed.
        var writeTimestampAtEntry = context.Property.TryGetWriteTimestamp();

        next(ref context);

        if (context.IsWritten ||
            context.Origin.Kind != ChangeOriginKind.FromSource ||
            !EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue) ||
            Equals(context.SentValue, context.NewValue))
        {
            return;
        }

        // Proven equality suppression: IsWritten == false alone does not prove it
        // (transaction capture also stops the chain), but the equality-plus-sent-differs
        // clauses show the projection landed exactly on the stored value, the one case
        // the [RunsFirst] equality handler suppresses before anything else can run.
        //
        // Read the observable value OUTSIDE the subject lock. Metadata.GetValue invokes
        // the getter, which may pass through read interceptors; running interceptor code
        // under Subject.SyncRoot would invert the codebase's getters-outside-locks
        // discipline (DerivedPropertyChangeHandler avoids exactly this against
        // LifecycleInterceptor). The correction deliberately carries the current
        // OBSERVABLE value (what any consumer or source reading the model sees), not a raw
        // backing-field read.
        var observedValue = context.Property.Metadata.GetValue?.Invoke(context.Property.Subject);

        SubjectPropertyChange correction;
        lock (context.Property.Subject.SyncRoot)
        {
            // Do the minimum under the lock: compare the raw write-timestamp against the
            // value captured at chain entry. A moved timestamp means a newer write landed
            // while we were deciding; that write is already flowing outbound, so drop the
            // correction. No getter runs here.
            //
            // This comparison is NECESSARY BUT NOT SUFFICIENT: write-timestamps have tick
            // granularity and GetTimestampFunction is user-settable, so two writes can share
            // a timestamp and an unchanged timestamp can hide a concurrent write. Safety rule:
            // on any doubt, DROP. Dropping is always safe because the only failure mode of a
            // dropped correction is a missing correction (the source stays diverged until its
            // next inbound event), never a wrong model value. The concurrency test asserts
            // drop-or-fresh, never stale.
            if (context.Property.TryGetWriteTimestamp() != writeTimestampAtEntry)
            {
                return;
            }

            // Timestamp unchanged: treat the window as clear and build the correction from the
            // observable value read above. A correction is a new local assertion: fresh local
            // timestamp, stamped on the property's write-timestamp metadata AND published, so
            // the source's echo (same value, same timestamp) is fully equality-suppressed
            // afterward.
            var timestampTicks = SubjectChangeContext.CaptureTimestamp();
            context.Property.SetWriteTimestamp(timestampTicks);

            correction = SubjectPropertyChange.Create(
                context.Property,
                ChangeOrigin.Correction(context.Origin.Source!),
                new DateTimeOffset(timestampTicks, TimeSpan.Zero),
                SubjectChangeContext.Current.ReceivedTimestamp,
                observedValue,
                observedValue);
        }

        _queue.EnqueueCorrection(in correction);
    }
}
```

Register it in the same extension that registers `PropertyChangeQueue` (follow how the queue itself is registered in `InterceptorSubjectContextExtensions.cs`, constructor-inject the queue instance). Note the equality-handler namespace for `[RunsBefore]` (`Namotion.Interceptor.Tracking.PropertyValueEqualityCheckHandler`).

- [ ] **Step 4: Run the tests, iterate until green, then run the full Tracking suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat: synthesize Correction changes for equality-suppressed diverged source values (#365)"
```

### Task 12: Correction delivery in ChangeQueueProcessor

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:145` (kind-aware dequeue skip) and the enqueue filter passed into `CreatePropertyChangeQueueSubscription` (kind-aware: `Correction` always enqueued)
- Test: `src/Namotion.Interceptor.Connectors.Tests` (delivery routing tests)

- [ ] **Step 1: Write the failing tests**

Three tests in the connectors test project, following the existing `ChangeQueueProcessor` test idiom:
1. A `Correction(S)` change is delivered by a processor whose `_source` is S (owner writes it back).
2. A `Correction(connection)` change is delivered by a processor whose `_source` is a DIFFERENT object (the WebSocket shape: origin identity is the connection, processor identity is the handler); this is the test that proves corrections are never dropped by identity mismatch.
3. A `FromSource(S)` change keeps today's routing (skipped by S's processor, delivered by others).
4. Dedup keeps kinds separate: within a flush batch for one property, a `Correction(S)` followed by a normal change resolves to the normal change (the correction is dropped); a normal change followed by a `Correction(S)` keeps the normal change (the correction never overwrites it); two normal changes still coalesce as today.

- [ ] **Step 2: Implement**

Replace the skip at line 145. Corrections are not echoes (no model change occurred), so they bypass the own-source skip entirely; property filters and connector topology determine actual recipients:

```csharp
if (change.Origin.Kind != ChangeOriginKind.Correction &&
    ReferenceEquals(change.Origin.Source, _source))
{
    continue;
}
```

Make the ENQUEUE-time filter from Task 6 kind-aware to match: `Correction` changes must always be enqueued (they bypass the own-source skip), so the predicate passed into `CreatePropertyChangeQueueSubscription` becomes `change => change.Origin.Kind == ChangeOriginKind.Correction || !ReferenceEquals(change.Origin.Source, _source)`. The enqueue filter and the dequeue skip stay in lockstep on the same rule, so a correction is never dropped at enqueue and the dequeue rule stays exactly as specified above.

Also guard the flush-time dedup at `ChangeQueueProcessor.cs:254` (`MergeWithNewer`) so it never coalesces across kinds: a `Correction` and a normal change for the same property must not merge. A newer normal change supersedes a pending correction (drop the correction, the normal change already carries the authoritative value outbound); a correction never overwrites a queued normal change. Only same-kind changes coalesce.

- [ ] **Step 3: Run connectors tests, then the full unit suite.**

Note: other queue subscribers (`PerformanceProfiler` in SamplesModel and ConnectorTester) also receive corrections; they only count changes, which is acceptable and needs no change. `PropertyChangeQueue` subscriptions are public API: document in Task 13 that subscribers filter on `Kind` if they only want model mutations.

- [ ] **Step 4: Correction-aware retry reapplication**

`ReapplyRetryQueue` in `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs:180` re-applies failed writes through the property setter. For a correction (old == new == current) the setter is a silent no-op: the equality check suppresses, no new queue event appears, and the drained retry entry is gone while the source stays diverged. Add kind-aware handling before the setter path.

Note the asymmetry with detection (Task 11): the detector injects via Tracking's internal `PropertyChangeQueue.EnqueueCorrection`, but `SubjectSourceBase` lives in `Namotion.Interceptor.Connectors` and cannot reach that internal method. A still-valid retry therefore does NOT re-inject into the global Tracking queue; it re-inserts the correction into the `ChangeQueueProcessor`'s own local pending buffer (a Connectors-level path that hands the change straight to the write pump), bypassing the setter, hooks, and INPC entirely:

```csharp
if (change.Origin.Kind == ChangeOriginKind.Correction)
{
    var current = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
    if (Equals(current, change.GetNewValue<object?>()))
    {
        // Still valid: re-insert into the ChangeQueueProcessor's own local pending
        // buffer (Connectors-level), NOT the global Tracking queue (unreachable from
        // here). No setter, no hooks, no INPC.
        EnqueueForProcessing(change);
    }
    // Model changed meanwhile: drop the stale correction; the newer model
    // change is already flowing outbound.
    continue;
}
```

Adapt `EnqueueForProcessing` to whatever `ChangeQueueProcessor`-level mechanism hands changes to the write pump's local pending buffer (follow the surrounding code); it must be the Connectors-level path, not a call back into the Tracking queue. Tests, in the connectors test project following the retry-queue test idiom: a failed correction enters the retry queue; on reconnect with the still-diverged value it is re-enqueued without invoking hooks or INPC and eventually reaches the source; if the model changed meanwhile, the stale correction is dropped.

- [ ] **Step 5: Run connectors tests, then commit both steps**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests
git add -A src
git commit -m "feat: correction-aware retry reapplication (#365)"
```

- [ ] **Step 4: Commit**

```bash
git add -A src
git commit -m "feat: deliver Correction changes only to the diverged source (#365)"
```

### Task 13: PR 2 docs, snapshots, and PR creation

**Files:**
- Modify: `docs/connectors.md` (append to the origin section)
- Modify: PublicApi snapshots (Correction enum member and factory)

- [ ] **Step 1: Docs**

Append to the origin section of `docs/connectors.md`:

```markdown
When an inbound value is projected by a hook to the value the model already stores (the
model at 100 receiving 105 with a clamp to 100), the equality check suppresses the write
and no ordinary change exists. The library then publishes a `Correction` change (old and
new value both the stored value) so the diverged source converges to the model. A
correction is a new local assertion: it carries a fresh local timestamp which is also
stamped on the property's write-timestamp metadata, so the source's echo returns the
same value and timestamp and is fully suppressed. No backing field is written and no
hooks or `PropertyChanged` fire. Corrections bypass the own-source echo skip and flow
through every change queue processor whose property filter matches: single-owner
sources converge to owner-only delivery, WebSocket broadcasts the authoritative
projection to all replicas, and re-writing an unchanged value to another bound source
is idempotent. Corrections never reach the change observable; direct change queue
subscribers do see them and should filter on `Origin.Kind` when only model mutations
are wanted. Confirmed transaction writes never produce corrections. A correction is
produced only when the source actively sends a diverging value; reasserting the model
to a silently diverged source is a reconciliation concern (#342).
```

- [ ] **Step 2: Accept PublicApi snapshot diffs** (must show only `Correction` additions).

- [ ] **Step 3: Full unit test run, push, create PR 2**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
git push -u origin feature/change-origin-corrections
gh pr create --base feature/change-origin-design --title "Correction origin kind for equality-suppressed diverged source values" --body "..."
```

PR body: the three-outcome matrix, delivery rule, docs pointer, `Closes #365`, and a note that `TriggeredBy` and `Presumed` remain tracked in #342. No AI attribution.

---

## Self-review checklist (run after writing, before handoff)

- Spec coverage: scenarios 1 to 8 all map to tasks (1-6 to Tasks 3-6/8, 7 to Task 7, 8 to Tasks 11-12). Docs per spec in Tasks 9 and 13. Bookkeeping in Tasks 10 and 13.
- The spec's "detection in the queue interceptor" wording is corrected by Task 11's ordering constraint (detector before the equality check); the spec file gets a matching one-line amendment.
- Type consistency: `ChangeOrigin.Correction(object)`, `PendingOrigin.Arm(PropertyReference, ChangeOrigin, object?)`, `SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?)`, `PropertyValidationContext<TProperty>(PropertyReference, TProperty, ChangeOrigin)` used identically across tasks.
- Arming reach: Connectors cannot call the internal `PendingOrigin.Arm` (core's `InternalsVisibleTo` excludes Connectors), so the appliers arm through the public `SetValueFromOrigin` for `FromSource`/`Confirmed` and keep the unarmed path for `Local`.
