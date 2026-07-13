# Typed ChangeOrigin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ambient source scope with a one-shot per-write origin stamp and a typed `ChangeOrigin` discriminator, then add the `Correction` kind for equality-suppressed inbound values.

**Architecture:** Origin is set per write in a thread-static slot, consumed by the matching `PropertyWriteContext` at construction, finalized at the terminal write (survival check: stored value must equal sent value), and published on `SubjectPropertyChange.Origin`. Timestamps stay ambient. Spec: `docs/superpowers/specs/2026-07-12-change-origin-design.md`.

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
- Produces: `internal static class PendingOrigin` with `internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue)` and `internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue)`; `internal readonly ref struct PendingOriginScope` whose `Dispose` restores the previous pending stamp. `PendingOrigin` and `PendingOriginScope` are internal by design (the raw one-shot slot never becomes public surface); the test project reaches them through `InternalsVisibleTo`.

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
    public void WhenSetAndConsumedWithMatchingProperty_ThenOriginAndSentValueAreReturned()
    {
        // Arrange
        var property = CreateProperty();
        var source = new object();

        // Act & Assert
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), "sent"))
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
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), null))
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
    public void WhenTargetDoesNotMatch_ThenConsumeReturnsLocalAndSlotStaysSet()
    {
        // Arrange
        var armedProperty = CreateProperty("Name");
        var otherProperty = CreateProperty("OtherName");

        using (PendingOrigin.Set(armedProperty, ChangeOrigin.FromSource(new object()), null))
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
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), null))
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
/// One-shot, per-write origin handoff. <see cref="Set"/> stores a pending stamp for exactly
/// one write of one property; the matching write chain consumes it at
/// <c>PropertyWriteContext</c> construction. Nested writes (hooks, INPC handlers, derived
/// recalculations) never inherit it: the slot is either already consumed or targets a
/// different property. The scope captures the previous frame and restores it on dispose
/// (a zero-allocation stack through nested ref structs, like SubjectChangeContextScope),
/// so a cancelled write cannot leak the stamp and a nested stamped write cannot destroy
/// an outer stamp. Same-property re-entry from OnChanging is unsupported (the inner
/// invocation consumes the stamp). Thread-static by design: set and consume happen
/// synchronously within one call frame, never across await. Internal: producers use
/// intent-level APIs (SetValueFromSource, ApplySubjectUpdate, transaction replay).
/// </summary>
internal static class PendingOrigin
{
    [ThreadStatic] private static bool _armed;
    [ThreadStatic] private static PropertyReference _target;
    [ThreadStatic] private static ChangeOrigin _origin;
    [ThreadStatic] private static object? _sentValue;

    internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue)
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
public void WhenNestedSetScopeIsDisposed_ThenOuterStampIsRestored()
{
    // Arrange
    var outerProperty = CreateProperty("Name");
    var innerProperty = CreateProperty("OtherName");
    var outerSource = new object();

    using (PendingOrigin.Set(outerProperty, ChangeOrigin.FromSource(outerSource), "outer"))
    {
        using (PendingOrigin.Set(innerProperty, ChangeOrigin.FromSource(new object()), "inner"))
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
Expected: 5 passed.

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

The test writes through a real subject with the pending origin set, observing `SubjectPropertyChange` is not available yet (Tracking changes come in Task 5), so observe via a probe write interceptor registered in the context:

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
    public void WhenWriteIsSetFromSource_ThenOriginIsFromSourceBeforeAndAfterWrite()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var person = new Person(context);
        var property = new PropertyReference(person, "Name");
        var source = new object();

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), "sent"))
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
        // Arrange: set the pending origin with sentValue "sent" but write a different
        // value, as a transforming hook or rewriting interceptor would.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var person = new Person(context);
        var property = new PropertyReference(person, "Name");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), "sent"))
        {
            person.Name = "transformed";
        }

        // Assert: attempted origin visible mid-chain, Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenWriteHasNoPendingOrigin_ThenOriginIsLocal()
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
/// origin (what the caller declared when setting the pending origin); when the terminal write lands (the same
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

Consuming the pending stamp at construction is a side effect: any direct construction of `PropertyWriteContext<TProperty>` (tests, benchmarks, not only the interceptor chain) drains the pending stamp. Both constructors MUST carry a doc comment stating this, so a caller who news up a context by hand knows it consumes the pending origin.

Add the finalization method to the struct:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal void FinalizeOrigin()
{
    // Typed comparison in the generic frame: NewValue is never boxed. SentValue arrives already
    // boxed from the inbound apply; cast it down to TProperty for EqualityComparer.Default. On the
    // non-generic path (TProperty == object) both operands are already boxed objects.
    if (Origin.Kind != ChangeOriginKind.Local &&
        !EqualityComparer<TProperty>.Default.Equals((TProperty)SentValue!, NewValue))
    {
        Origin = ChangeOrigin.Local;
    }
}
```

Finalization and the survival check live in the terminal write delegates of `WriteInterceptorFactory` (both the no-interceptor variant and the chain variant), NOT in `InterceptorExecutor`. In `WriteInterceptorFactory.cs`, in BOTH terminal delegates, immediately after `context.IsWritten = true;` (which already runs under `context.Property.Subject.SyncRoot`) add:

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

### Task 4: Timestamps-only SubjectChangeContext and pending-origin SetValueFromSource

**Files:**
- Modify: `src/Namotion.Interceptor/SubjectChangeContext.cs` (remove `Source` field, `WithSource`, `WithState`; add `WithTimestamps`)
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectChangeContextExtensions.cs` (add public `SetValueFromOrigin` primitive that sets the pending origin instead of scoping; `SetValueFromSource` forwards to it)
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs:357` (delete the `WithSource(null)` using, keep its body)
- Test: existing suites; update `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Produces: `public static SubjectChangeContextScope WithTimestamps(DateTimeOffset? changed, DateTimeOffset? received)`; `SubjectChangeContext` no longer has `Source`. New public Tracking primitive `SubjectChangeContextExtensions.SetValueFromOrigin(this PropertyReference property, ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? value)` that sets the pending origin for any origin kind; `SetValueFromSource` forwards to it.
- Consumes: `PendingOrigin.Set` (Task 2).

Core's `InternalsVisibleTo` covers Tracking, the tests, and the benchmark project, but NOT `Namotion.Interceptor.Connectors`, so the Connectors update appliers (Task 6) cannot call the internal `PendingOrigin.Set`. `SetValueFromOrigin` is their public intent-level entry point: it performs the write itself, so the raw slot stays internal.

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

Add a public `SetValueFromOrigin` primitive that sets the pending origin for any origin kind and performs the write, then make `SetValueFromSource` a thin forwarder. Connectors cannot reach the internal `PendingOrigin.Set`, so this public Tracking extension is the appliers' entry point (see Task 6). Contract: when `receivedTimestamp` is null, `SetValueFromOrigin` preserves the ambient received timestamp (via the `WithTimestamps` null fallback from Step 1) rather than overwriting it with the null sentinel; only a non-null `receivedTimestamp` replaces the ambient value. This is what keeps the applier path (which passes null received) behavior-identical to master's `WithChangedTimestamp` wrapping:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static void SetValueFromOrigin(
    this PropertyReference property,
    ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
    object? value)
{
    using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
    using (PendingOrigin.Set(property, origin, value))
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
git commit -m "feat: make SubjectChangeContext timestamps-only, set pending origin in SetValueFromSource (#345)"
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

In `SubjectPropertyChange.cs`: replace `public object? Source { get; }` with a single `ChangeOrigin Origin` field, the baseline layout:

```csharp
public ChangeOrigin Origin { get; }
```

Thread `ChangeOrigin origin` through the private constructor and `Create` (replacing `object? source`, storing `origin` directly), and replace the `WithSource(object? source)` with-er with:

```csharp
public SubjectPropertyChange WithOrigin(ChangeOrigin origin) =>
    new(Property, origin, ChangedTimestamp, ReceivedTimestamp, _oldValueStorage, _newValueStorage, _oldBoxedHolder, _newBoxedHolder);
```

Also fix `MergeWithNewer`: it currently copies only `newerChange.Source` into the merged change, so a deduplicated change silently loses its kind and reverts to `Local`. Copy the newer change's full `Origin`:

```csharp
public SubjectPropertyChange MergeWithNewer(SubjectPropertyChange newerChange) =>
    new(Property, newerChange.Origin, newerChange.ChangedTimestamp, newerChange.ReceivedTimestamp,
        _oldValueStorage, newerChange._newValueStorage, _oldBoxedHolder, newerChange._newBoxedHolder);
```

Add a merge-origin test in the Tracking test project: merging a `FromSource(S)` change with a newer `FromSource(S)` change leaves the result `Origin.Kind == FromSource` and `Origin.Source == S` (both kind and source survive the merge).

Add a size-regression test in the Tracking test project. The plain `ChangeOrigin` field (kind byte plus source reference) may cost one alignment slot (8 bytes) versus master's `object? Source`; that growth is the accepted baseline, so the bound allows it:

```csharp
[Fact]
public void WhenMeasuringSubjectPropertyChange_ThenSizeStaysWithinOneAlignmentSlotOfMaster()
{
    // The plain ChangeOrigin field may cost one alignment slot (8 bytes) versus master's
    // object? Source. That growth is accepted. If the benchmark gate later shows it matters
    // on the hot path, flatten the origin into a padding-folded kind byte plus the existing
    // source reference slot; that is the known optimization, not applied preemptively.
    Assert.True(Unsafe.SizeOf<SubjectPropertyChange>() <= 104,
        $"SubjectPropertyChange grew to {Unsafe.SizeOf<SubjectPropertyChange>()} bytes");
}
```

First measure the actual master size with a throwaway `Unsafe.SizeOf` print on master, then set the bound to that measured size plus one alignment slot (8 bytes); 104 above is a placeholder assuming a master size of 96.

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
using (PendingOrigin.Set(change.Property, change.Origin, change.GetNewValue<object?>()))
{
    metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
}
```

Setting a `Local` origin is a no-op stamp (consume yields Local), so revert paths re-apply each change with its original provenance without branching.

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
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` and `SubjectItemsUpdateApplier.cs` (thread `origin` to every property write site; each site currently wrapped in `WithChangedTimestamp` applies via `SetValue` or `RegisteredSubjectProperty`; when `origin.Kind != ChangeOriginKind.Local`, apply through `SetValueFromOrigin(origin, propertyUpdate.Timestamp, null, value)`, the public Tracking primitive from Task 4 that sets the pending origin for `FromSource` and `Confirmed` alike, since Connectors cannot reach the internal `PendingOrigin.Set`; for `Local`, keep the current unstamped path, since `Local` is the default and needs no stamp). The five sites (`SubjectUpdateApplier.cs:84,131,139` and `SubjectItemsUpdateApplier.cs:124,209`) are each wrapped in `WithChangedTimestamp(propertyUpdate.Timestamp)` today; `propertyUpdate.Timestamp` MUST be passed as the `changedTimestamp` argument of `SetValueFromOrigin` at every site, or the inbound changed-timestamp is silently replaced with capture-time `UtcNow` and provenance is corrupted
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs:200-207` (pass `ChangeOrigin.FromSource(connection)`), `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs:296-299,540-543` (pass `ChangeOrigin.FromSource(this)` / `ChangeOrigin.FromSource(state.source)`); drop the `WithSource` usings; correct the lock comment at `WebSocketSubjectHandler.cs:200` to say the lock serializes update application, not that thread-static state requires it
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/Verification/FailureDiagnostics.cs:175` (pass `ChangeOrigin.Local`)
- Test: `src/Namotion.Interceptor.Connectors.Tests` update-apply suites gain a source overload test and a timestamp-preservation test (an applied update's published change carries `propertyUpdate.Timestamp`, not capture-time `UtcNow`)

PR 1 is deliberately lifecycle-neutral (see the spec's "Source lifecycle (PR 1)"): this task does NOT touch `SubjectSourceBase`, the `ChangeQueueProcessor` lifecycle, `PropertyChangeQueueSubscription`, or subscription creation. There is no subscription reorder, no enqueue-time filter parameter, no bounded load-window cap, no drop counter, and no divergence warning. The initial-load write-back gap is closed generally by open PR #355; keeping PR 1 lifecycle-neutral lets #366 merge before or after #355 with no conflicts. The steady-state own-source echo skip stays the existing dequeue-time comparison in `ChangeQueueProcessor.ProcessAsync` (already updated to `ReferenceEquals(change.Origin.Source, _source)` in Task 5).

**Interfaces:**
- Produces: `ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectFactory? subjectFactory, ChangeOrigin origin, Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)`.

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

Add the required `ChangeOrigin origin` parameter, thread it through `SubjectUpdateApplier.ApplyUpdate` and `SubjectItemsUpdateApplier` down to the write sites, and route stamped (`FromSource`/`Confirmed`) writes through `SetValueFromOrigin`; keep the current unstamped path for `Local`. Update the three WebSocket sites and the ConnectorTester site. Also update the code sample in the XML doc comment of `ApplySubjectUpdate` if present.

- [ ] **Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests && dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: all pass, including WebSocket echo-suppression integration tests (they prove end-to-end equivalence of the new stamping). Steady-state echo suppression is unchanged (own-source changes are not written back; other-source and local changes are), since the dequeue-time skip from Task 5 is untouched here.

- [ ] **Step 5: Commit**

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

Run `dotnet test src/Namotion.Interceptor.slnx --filter "FullyQualifiedName~PublicApi"`; for each failure, inspect the diff (must show exactly: `ChangeOrigin`, `ChangeOriginKind`, `PropertyWriteContext.Origin`, `WithTimestamps` added; `Source`, `WithSource`, `WithState`, old validator signature removed), then copy `.received.txt` over `.verified.txt`. `PendingOrigin` and `PendingOriginScope` are internal and must NOT appear in any public API snapshot; if either shows up in a diff, an accessibility modifier is wrong.

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

### Task 11: Branch, Correction kind, detection in SetValueFromOrigin

**Files:**
- Branch: `git checkout -b feature/change-origin-corrections feature/change-origin-design`
- Modify: `src/Namotion.Interceptor/ChangeOrigin.cs` (add `Correction = 3` and factory)
- Modify: `src/Namotion.Interceptor/PendingOrigin.cs` (add the internal thread-static write-outcome slot: `SetOutcome` / `ClearOutcome` / `TryTakeOutcome`)
- Modify: `src/Namotion.Interceptor.Tracking/PropertyValueEqualityCheckHandler.cs` (record the outcome for stamped writes: `(isWritten: false, valueUnchanged: true)` on suppression, `(context.IsWritten, valueUnchanged: false)` on pass-through). No core file is modified.
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectChangeContextExtensions.cs` (correction detection inside `SetValueFromOrigin`, reading the outcome after the setter returns, for `FromSource` writes)
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs` (internal `EnqueueCorrection`)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/SourceCorrectionTests.cs`

**Interfaces:**
- Produces: `ChangeOriginKind.Correction`, `ChangeOrigin.Correction(object source)`, `PendingOrigin.SetOutcome(bool isWritten, bool valueUnchanged)`, `PendingOrigin.ClearOutcome()` (internal, wipes the outcome slot; called on entry by `SetValueFromOrigin`), and `PendingOrigin.TryTakeOutcome(out bool isWritten, out bool valueUnchanged)` (internal, thread-static, `TryTakeOutcome` clears the slot), `PropertyChangeQueue.EnqueueCorrection(in SubjectPropertyChange)` (internal). The outcome record has two fields, no finalized origin: `SetValueFromOrigin` re-derives kind and source from its own `origin` argument, so a stored origin would be dead storage. The outcome is recorded by `PropertyValueEqualityCheckHandler` itself, not by any core file. Correction detection is folded into the existing `SetValueFromOrigin` primitive (no new public surface, no new interceptor), hard-dependent on `PropertyValueEqualityCheckHandler` structurally: the outcome API is internal and that handler is its only producer, so an outcome with `valueUnchanged: true` in hand proves the handler ran; no registration check, service lookup, or per-context caching is performed. No non-destructive peek of the pending slot is added: the outcome record already distinguishes a cancelled write (no outcome) from a suppressed one.

**Why detection lives in `SetValueFromOrigin` (not a new write interceptor):** a dedicated interceptor was rejected because it would add an interceptor frame to every write and need `[RunsFirst]`/`[RunsBefore]` ordering guarantees against `PropertyValueEqualityCheckHandler`, whereas `SetValueFromOrigin` already brackets exactly the one stamped write in question and adds nothing to the write chain. Detection keys off a thread-static outcome record, not off timestamps or a transaction flag. The outcome is recorded by the existing `PropertyValueEqualityCheckHandler` itself, gated on the write being stamped (`context.Origin.Kind != Local`): on suppression it records `(isWritten: false, valueUnchanged: true)`, on pass-through `(context.IsWritten` after `next`, `valueUnchanged: false)`, where `valueUnchanged` is the handler's own `EqualityComparer<TProperty>.Default.Equals(CurrentValue, NewValue)` comparison in its generic `TProperty` frame. `InterceptorExecutor` is NOT modified: PR 2 touches no core files, mirroring PR 1's untouched generator. Recording in the handler makes the dependency on it structural: the outcome API is internal and the handler is its only producer, so an outcome in hand proves the handler ran, no explicit registration check is needed, and the transaction self-exclusion argument becomes exact because it reasons about this handler's own comparison. `SetValueFromOrigin` reads and clears that record after the setter returns. This replaces the earlier guard-based design, whose timestamp signal broke under null-timestamp scopes, a frozen `GetTimestampFunction`, and same-tick writes, and whose transaction guard was a process-global counter that let unrelated concurrent transactions drop legitimate corrections.

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
        // Act: property.SetValueFromOrigin(ChangeOrigin.Confirmed(source), null, null, 105);
        //      the hook projects to 100 and the equality check suppresses.
        // Assert: no correction. Detection only runs for origin.Kind == FromSource, so a
        //         Confirmed apply is never a correction candidate (the commit protocol
        //         already guarantees the source state).
    }

    [Fact]
    public void WhenInboundStampedWriteIsCapturedByTransaction_ThenNoCorrectionIsPublished()
    {
        // Arrange: model at 50, an active SubjectTransaction on the context.
        // Act: SetValueFromSource(source, null, null, 80); the transaction interceptor
        //      captures the value and stops the chain, so the terminal write never lands.
        // Assert: no correction in the queue; the value is pending in the transaction.
        //         This pins the valueUnchanged self-exclusion: the equality handler runs
        //         [RunsFirst], so a transaction only captures when the values differ, making
        //         the outcome's valueUnchanged false, so a captured-but-unwritten value never
        //         triggers synthesis. No process-global transaction flag is consulted.
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
        //          SetValueFromOrigin's synthesis (use ManualResetEventSlim inside the hook).
        // Act: thread 1 applies 105 from the source (projects to 100, suppressed);
        //      thread 2 writes 90 locally while thread 1 is parked in the hook.
        // Assert: drop-or-fresh, never stale. The correction is either absent (dropped
        //         because the write-timestamp moved under the lock) or carries the fresh
        //         post-write value (90); it never carries the stale 100.
    }
}
```

Write the bodies fully against the tracking test idioms (queue subscription via `context.CreatePropertyChangeQueueSubscription()`). No detector is registered anywhere: detection is folded into `SetValueFromOrigin`, so every test just builds a tracking context with a queue subscription and drives the apply through `SetValueFromSource` / `SetValueFromOrigin`.

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

`PendingOrigin.cs`: add a thread-static write-outcome slot. `PropertyValueEqualityCheckHandler` records the outcome of a stamped write; `SetValueFromOrigin` clears the slot on entry (before invoking the setter, unconditionally, for every origin kind including `Confirmed`) and then reads and clears it after the setter returns. The outcome is a two-field record (`isWritten`, `valueUnchanged`), no finalized origin: `SetValueFromOrigin` re-derives kind and source from its own `origin` argument. The clear-on-entry is load-bearing: a commit replay writes through `PendingOrigin.Set` directly, so the equality handler records its `Confirmed` outcome but no primitive ever consumes it; without the clear-on-entry that stale outcome, or any earlier write's outcome on the same thread, could be misread by a later cancelled `FromSource` write as its own. Two invariant notes: the generated setter already gates `OnXChanged`/INPC on the write result, but detection must never lean on that instead of the clear-on-entry, which is the primitive that actually guarantees no stale outcome survives into the next stamped apply; and a null-context subject writes its backing field raw without reaching the interceptor chain, so no outcome is recorded for it and clear-on-entry guarantees no misattribution. This replaces the removed non-destructive slot peek: a cancelled write records no outcome and self-excludes, and a transaction capture records `valueUnchanged == false`.

```csharp
[ThreadStatic] private static bool _hasOutcome;
[ThreadStatic] private static bool _outcomeIsWritten;
[ThreadStatic] private static bool _outcomeValueUnchanged;

internal static void SetOutcome(bool isWritten, bool valueUnchanged)
{
    _hasOutcome = true;
    _outcomeIsWritten = isWritten;
    _outcomeValueUnchanged = valueUnchanged;
}

// Clear-on-entry: wipe any outcome a prior write on this thread may have left before the next
// stamped setter runs, so a later cancelled write can never misread a leaked outcome as its own.
internal static void ClearOutcome()
{
    _hasOutcome = false;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal static bool TryTakeOutcome(out bool isWritten, out bool valueUnchanged)
{
    if (_hasOutcome)
    {
        isWritten = _outcomeIsWritten;
        valueUnchanged = _outcomeValueUnchanged;
        _hasOutcome = false;
        return true;
    }

    isWritten = false;
    valueUnchanged = false;
    return false;
}
```

`PropertyValueEqualityCheckHandler.cs`: record the outcome inside the existing handler, gated on the write being stamped. This is the only write-path change PR 2 makes, and it modifies no core file. On suppression (values already equal, the handler stops the chain) record `(isWritten: false, valueUnchanged: true)`; on pass-through (values differ, the handler calls `next`) record `(context.IsWritten, valueUnchanged: false)` after `next` returns. A local write is not stamped and records nothing:

```csharp
public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
{
    // Only stamped (inbound) writes record an outcome for correction detection.
    var stamped = context.Origin.Kind != ChangeOriginKind.Local;

    if (EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
    {
        // Suppressed: values already equal. A stamped suppressed write is the correction candidate.
        if (stamped)
        {
            PendingOrigin.SetOutcome(isWritten: false, valueUnchanged: true);
        }

        return;
    }

    next(ref context);

    if (stamped)
    {
        PendingOrigin.SetOutcome(context.IsWritten, valueUnchanged: false);
    }
}
```

`valueUnchanged` is the handler's own typed comparison, so no separate comparison is added anywhere. A landed transform (values differed, the terminal wrote, `Origin` finalized to `Local`) records `isWritten: true`, so `SetValueFromOrigin` correctly treats it as no correction. The derived-recalc cascade and every other local write run `Local` and record nothing.

`SubjectChangeContextExtensions.cs`: fold correction detection into `SetValueFromOrigin`. Read and clear the outcome after the setter returns, and synthesize only for `FromSource` writes whose outcome says the terminal never landed (`!isWritten`) and the equality handler suppressed the write (`valueUnchanged`). No `using Namotion.Interceptor.Tracking.Transactions;` is needed anymore; detection no longer consults `SubjectTransaction`:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static void SetValueFromOrigin(
    this PropertyReference property,
    ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
    object? value)
{
    // Clear-on-entry, unconditional and for every origin kind (including Confirmed): a commit
    // replay records a Confirmed outcome via the equality handler but never consumes it through
    // this primitive, so wipe any stale outcome before this setter runs. Without it a later
    // cancelled FromSource write could misread a leaked outcome as its own.
    PendingOrigin.ClearOutcome();

    var stamped = origin.Kind != ChangeOriginKind.Local;

    // Baseline for the synthesis concurrency race ONLY (not detection): captured before the
    // setter so a concurrent write during synthesis moves it and we drop on doubt.
    var writeTimestampBaseline = stamped ? property.TryGetWriteTimestamp() : null;

    using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
    using (PendingOrigin.Set(property, origin, value))
    {
        property.Metadata.SetValue?.Invoke(property.Subject, value);
    }

    if (!stamped)
    {
        return;
    }

    // Read and clear the outcome the equality handler recorded for this stamped write. No outcome
    // means the chain never ran (an OnXChanging hook cancelled the write, or the equality handler
    // is not registered): nothing to correct.
    if (!PendingOrigin.TryTakeOutcome(out var isWritten, out var valueUnchanged))
    {
        return;
    }

    // Correction candidate: FromSource, terminal never landed, equality-suppressed. A transaction
    // capture has valueUnchanged == false (it only captures when values differ), so it self-excludes,
    // and no process-global transaction flag is consulted. Timestamps play no role in this decision.
    // The dependency on PropertyValueEqualityCheckHandler is structural, not checked: the outcome
    // API is internal and that handler is its only producer, so valueUnchanged == true in hand
    // proves the handler ran with its [RunsFirst] ordering, which is what makes the transaction
    // self-exclusion hold. Without the handler no outcome is ever recorded and TryTakeOutcome
    // already returned false above, so a captured equal-value write can never synthesize a
    // spurious idempotent correction.
    if (origin.Kind == ChangeOriginKind.FromSource && !isWritten && valueUnchanged)
    {
        DetectAndEnqueueCorrection(property, origin.Source!, value, writeTimestampBaseline);
    }
}

private static void DetectAndEnqueueCorrection(
    PropertyReference property, object source, object? sentValue, DateTimeOffset? writeTimestampBaseline)
{
    // The stamped write was equality-suppressed. Read the observable value OUTSIDE the subject
    // lock (the getter may run read interceptors; running them under Subject.SyncRoot would invert
    // the codebase's getters-outside-locks discipline, which DerivedPropertyChangeHandler avoids
    // against LifecycleInterceptor). If it still equals the sent value there is no divergence
    // (pure echo) and no correction; the correction deliberately carries the OBSERVABLE value.
    var observedValue = property.Metadata.GetValue?.Invoke(property.Subject);
    if (Equals(sentValue, observedValue))
    {
        return;
    }

    // Resolve the queue from the subject's context (SetValueFromOrigin is static). No queue
    // means no delivery target, so nothing to synthesize.
    var queue = property.Subject.Context.TryGetService<PropertyChangeQueue>();
    if (queue is null)
    {
        return;
    }

    SubjectPropertyChange correction;
    lock (property.Subject.SyncRoot)
    {
        // Concurrency drop-on-doubt: a newer write may have landed while we decided. Compare the
        // raw write-timestamp against the baseline under the lock; a moved timestamp means that
        // write is already flowing outbound, so drop. NECESSARY BUT NOT SUFFICIENT (tick
        // granularity, user-settable GetTimestampFunction), so on any doubt DROP: the only
        // failure mode of a dropped correction is a missing one (the source stays diverged
        // until its next inbound event), never a wrong model value.
        if (property.TryGetWriteTimestamp() != writeTimestampBaseline)
        {
            return;
        }

        // Fresh local assertion: stamp a new write-timestamp on the metadata AND publish the
        // correction with it, so the source's echo (same value, same timestamp) is fully
        // equality-suppressed afterward.
        var timestampTicks = SubjectChangeContext.CaptureTimestamp();
        property.SetWriteTimestamp(timestampTicks);

        correction = SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Correction(source),
            new DateTimeOffset(timestampTicks, TimeSpan.Zero),
            SubjectChangeContext.Current.ReceivedTimestamp,
            observedValue,
            observedValue);
    }

    // Enqueue after releasing the lock; corrections never touch PropertyChangeObservable.
    queue.EnqueueCorrection(in correction);
}
```

There is no registration step and no new interceptor: detection is part of `SetValueFromOrigin`, which is already on every `FromSource` apply path (`SetValueFromSource` forwards to it, and the Task 6 appliers route stamped writes through it). A purely local application never calls it with a `FromSource` origin, so it synthesizes nothing and pays only the single-branch stamped-write check in `PropertyValueEqualityCheckHandler` on its writes, with nothing to gate. The Tracking-level `SourceCorrectionTests` therefore register no detector: they build a tracking context with a queue subscription and drive applies through `SetValueFromSource` / `SetValueFromOrigin`.

- [ ] **Step 4: Run the tests, iterate until green, then run the full Tracking suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat: synthesize Correction changes for equality-suppressed diverged source values (#365)"
```

### Task 12: Correction delivery in ChangeQueueProcessor

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:145` (kind-aware dequeue skip: `Correction` bypasses the own-source skip), the flush-time dedup at `ChangeQueueProcessor.cs:254` (normal-beats-correction, explicit kind branch), the immediate path (`bufferTime <= 0`) which drops `Correction` changes with a warning log instead of writing them (buffered-only correction delivery), and per-correction send-time revalidation in the correction write branch: pre-write, re-read the property getter immediately before writing each `Correction` and drop it if the model value no longer equals the correction's value (cheap early drop, not the correctness guarantee); post-write, after each correction write returns re-read the model value and, if it no longer equals what was just written, write the current model value to the source as a follow-up correction in a bounded loop (small fixed attempt limit; on exhaustion drop with a warning naming `RequestResynchronization`/#342)
- Modify: `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs` (skip `Origin.Kind == Correction` in the sole public enqueue entry point, the batch `Enqueue(ReadOnlyMemory<SubjectPropertyChange> changes)`, filtering each item in the span, with a debug log: a failed correction write is never retried, and no batch API can smuggle a correction into the queue)
- Test: `src/Namotion.Interceptor.Connectors.Tests` (delivery routing tests, dedup tests, immediate-mode drop, and one retry test)

- [ ] **Step 1: Write the failing tests**

Tests in the connectors test project, following the existing `ChangeQueueProcessor` test idiom:
1. A `Correction(S)` change is delivered by a processor whose `_source` is S (owner writes it back).
2. A `Correction(connection)` change is delivered by a processor whose `_source` is a DIFFERENT object (the WebSocket shape: origin identity is the connection, processor identity is the handler); this is the test that proves corrections are never dropped by identity mismatch.
3. A `FromSource(S)` change keeps today's routing (skipped by S's processor, delivered by others).
4. Normal-beats-correction dedup: within a flush batch for one property, a `Correction(S)` followed by a normal change resolves to the normal change (the correction is dropped) and the normal change keeps its own old value as the diff baseline; a normal change followed by a `Correction(S)` keeps the normal change (the correction never replaces it); two normal changes still coalesce as today; two corrections for one property coalesce into one (same value by construction). The rule is normal-beats-correction regardless of queue order.
5. Immediate-mode drop: a processor constructed with `bufferTime <= 0` never writes a `Correction` to its source and logs a warning, while a normal change on the same processor is still written. This pins the buffered-only delivery of corrections: the immediate path has no dedup, so a stale correction enqueued after a concurrent normal change could otherwise leave the source at a wrong value, which dropping-with-warning prevents.
6. Retry: a failed correction write is not retried (the batch `WriteRetryQueue.Enqueue` filters `Correction` changes out per item) and a later inbound event converges the source.
7. Send-time revalidation, pre-write drop: a stale correction that survived synthesis and dedup is dropped before its write. Enqueue a `Correction(S)` for a property, move the model to a different value with a normal write before the flush writes the correction, then assert the correction is never written to the source (the getter no longer equals the correction's value) while the normal change is. This exercises only the case where the model already moved BEFORE revalidation.
8. Send-time revalidation, in-flight window: an inbound `FromSource(S)` apply lands between the pre-write revalidation and the completion of the correction write. Use a gate (`ManualResetEventSlim`) in a fake source's write path to hold the correction write open, apply the inbound value while it is held, then release; assert a follow-up write from the post-write recheck converges the source to the newer value and no stale value remains on the source. This is the test the pre-write-only drop (test 7) cannot cover, because the echo-suppressed inbound-from-S change never enters S's outbound queue.
9. Send-time revalidation, bounded-loop exhaustion: continuous racing writes that keep moving the model during each post-write recheck exhaust the attempt limit, and the correction is dropped with a warning naming `RequestResynchronization`/#342; assert the warning is logged and the loop terminates.
10. Correction timestamp at the processor: the correction written to the source carries the same fresh `ChangedTimestamp` that synthesis stamped on the property's write-timestamp metadata. No new connector integration tests: MQTT and OPC UA outbound timestamp serialization is already covered by the existing connector suites, and corrections ride the same outbound write path.

- [ ] **Step 2: Implement**

Replace the skip at line 145. Corrections are not echoes (no model change occurred), so they bypass the own-source skip entirely; property filters and connector topology determine actual recipients:

```csharp
if (change.Origin.Kind != ChangeOriginKind.Correction &&
    ReferenceEquals(change.Origin.Source, _source))
{
    continue;
}
```

This dequeue-time skip is the only echo suppression (PR 1 is lifecycle-neutral, so there is no enqueue-time filter to keep in lockstep). `Correction` changes bypass the own-source skip and reach every eligible processor.

Also make the flush-time dedup at `ChangeQueueProcessor.cs:254` (`MergeWithNewer`) kind-aware with an explicit kind branch. The flush dictionary keeps one entry per property; the rule is normal-beats-correction regardless of queue order: a normal change supersedes a pending correction for that property (drop the correction, the normal change already carries the authoritative value outbound), and a correction never replaces a queued normal change. When a normal change supersedes a correction, the normal change's own old value stays the diff baseline: a correction carries `old == new` and contributes nothing to a diff, so it must not overwrite the baseline the superseding normal change needs. Corrections coalesce only with corrections, which is safe because two corrections for one property carry the same value by construction. The immediate path (`bufferTime <= 0`) performs no dedup, so it cannot safely write corrections: a stale correction enqueued after a concurrent normal change could push the source to a wrong value. In immediate mode the processor drops every `Correction` with a warning log instead of writing it; the missing-correction failure mode is safe (the source stays diverged until its next inbound event) whereas a wrong write is not.

Finally, add per-correction send-time revalidation to the correction write branch, both before and after each correction write. Each correction in a flushed batch is revalidated individually; the pre-write check is a cheap early drop and the post-write recheck is the correctness guarantee.

Pre-write, immediately before writing a `Correction` to the source, re-read the current model value through the property metadata getter and drop the correction if it no longer equals the correction's value. This catches the common case where the model already moved before the write started (and the synthesis-time write-timestamp check stays as an even cheaper early-out), but it is NOT sufficient on its own. One interleaving defeats it: a `Correction(S, 100)` passes pre-write revalidation; before it reaches S, S sends 90; the model stores 90 as `FromSource(S)`; that inbound change is echo-suppressed for S's own processor (the own-source skip), so it never enters S's outbound queue; the accepted correction then overwrites S with a stale 100, leaving model 90 and source 100 permanently. The earlier ordering argument (the newer change follows the correction through the same serialized outbound queue) holds only for LOCAL racing writes, which enqueue a following normal change delivered to S; an inbound-from-S write is echo-suppressed for S and never follows.

Post-write is therefore the correctness guarantee. After each correction write to S returns, re-read the current model value; if it no longer equals the value just written, the model changed during flight (the dangerous case is an inbound apply from S itself, since a local write in flight enqueues its own following normal change that reaches S), so write the current model value to S as a follow-up correction, in a bounded loop with a small fixed attempt limit; on exhaustion drop with a warning naming `RequestResynchronization`/#342 as the recovery path. Window analysis: an inbound from S arriving AFTER the post-write re-read means S already holds that value itself (it sent it), so the echo skip is then correct and no divergence exists; the recheck closes exactly the dangerous window (an inbound from S landing between the pre-write read and the completion of the correction write) and the source converges at quiescence. This needs no serialization between inbound applies and outbound writes, no per-property in-flight tracking, and no conditional source writes.

```csharp
// In the correction write branch, per correction. Pre-write revalidation (cheap early drop):
var correctionValue = change.GetNewValue<object?>();
if (!Equals(change.Property.Metadata.GetValue?.Invoke(change.Property.Subject), correctionValue))
{
    // The model already moved before the write started; its newer change is flowing outbound
    // on its own. Drop the stale correction.
    continue;
}

// Write the correction, then post-write revalidation in a bounded loop (the correctness guarantee):
var attempt = 0;
while (true)
{
    await WriteCorrectionToSourceAsync(change, correctionValue, cancellationToken).ConfigureAwait(false);

    // Post-write re-read: if the model no longer equals what we just wrote, an inbound apply from
    // this source landed during the write (a local write in flight would have enqueued its own
    // following normal change), so re-assert the current model value as a follow-up correction.
    var modelValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
    if (Equals(modelValue, correctionValue))
    {
        break; // converged: source holds the current model value
    }

    if (++attempt >= MaxCorrectionRevalidationAttempts)
    {
        _logger.LogWarning(
            "Correction for {Property} did not converge after {Attempts} attempts; dropping. " +
            "The source recovers on its next inbound event or an explicit RequestResynchronization (#342).",
            change.Property, attempt);
        break;
    }

    correctionValue = modelValue; // follow-up correction to the newer model value
}
```

- [ ] **Step 3: Run connectors tests, then the full unit suite.**

Note: other queue subscribers (`PerformanceProfiler` in SamplesModel and ConnectorTester) also receive corrections; they only count changes, which is acceptable and needs no change. `PropertyChangeQueue` subscriptions are public API: document in Task 13 that subscribers filter on `Kind` if they only want model mutations.

- [ ] **Step 4: Keep corrections out of the retry queue**

A failed correction write must not be retried. Reapplying a correction through the property setter is a silent no-op (current equals old equals new, the equality check suppresses, no new queue event appears), and re-sending a stale correction raw could push the source to a value the model has since moved off of. The filter must cover EVERY enqueue entry point of `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`, so a correction can never enter retries through a batch API. Enumerate the entry points before filtering:

- `Enqueue(ReadOnlyMemory<SubjectPropertyChange> changes)` (public, batch): the sole public way changes enter the queue. This is where the filter lives, applied per item in the span, so no batch call can smuggle a correction into `_pendingWrites`.
- `RequeueChanges(ReadOnlySpan<SubjectPropertyChange> changes)` (private, front-insert on flush failure): only ever re-inserts items already dequeued from `_pendingWrites`, which were already filtered on the way in through `Enqueue`, so no correction can reach it. No second filter is needed here, but state the invariant so a future edit that starts requeueing externally-supplied changes revisits it.
- `FlushAsync(...)` and `DrainForLocalReapply()`: neither adds new changes to the queue (one flushes, one drains), so neither needs a filter.

There is no single-item `Enqueue` overload; filter each item inside the existing batch loop, with a debug log:

```csharp
public void Enqueue(ReadOnlyMemory<SubjectPropertyChange> changes)
{
    if (_maxQueueSize is 0)
    {
        _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes.", changes.Length);
        return;
    }

    int droppedCount;
    lock (_lock)
    {
        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Origin.Kind == ChangeOriginKind.Correction)
            {
                // A failed correction write is not retried: the source converges on its next inbound
                // event. Retrying would either no-op through the equality check or re-assert a value
                // the model has since moved off of (a wrong write). Dropping keeps the only failure
                // mode a missing correction, never a wrong one.
                _logger.LogDebug("Dropping failed correction write from the retry queue for {Property}.", span[i].Property);
                continue;
            }

            _pendingWrites.Add(span[i]);
        }

        // ... existing ring-buffer trim and count update unchanged
    }

    // ... existing drop-count warning unchanged
}
```

This drops a failed correction, and the source converges on its next inbound event, the same accepted outcome as every other correction-drop path (immediate mode, dedup supersession, drop-on-doubt, pre-write revalidation, and post-write bounded-loop exhaustion). It replaces the earlier kind-aware re-checks in both the reconnect (`ReapplyRetryQueue`) and steady-state (`WriteRetryQueue.FlushAsync`) retry paths, neither of which this design adds. Once #355's origin-agnostic 3-way reconcile lands it handles corrections generically anyway (a correction has model equal to old equal to new, so its restore-and-send branch is a no-op), making this filter harmlessly redundant but still correct in any merge order.

Test, in the connectors test project following the retry-queue test idiom: a batch containing a correction and a normal change is enqueued, the correction is skipped while the normal change is retained (nothing correction-shaped enters the queue and no hooks or INPC fire), and a later inbound event converges the source.

- [ ] **Step 5: Run connectors tests, then commit**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests
git add -A src
git commit -m "feat: deliver Correction changes to matching processors and skip them on retry (#365)"
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
is idempotent. Corrections are delivered only through the buffered flush path; a change
queue processor configured in immediate mode (no buffer) drops corrections with a
warning instead of writing them, because the immediate path has no dedup to guard a
stale correction racing a concurrent normal change. Corrections never reach the change
observable; direct change queue subscribers do see them and should filter on
`Origin.Kind` when only model mutations are wanted. Confirmed transaction writes never produce corrections. A correction is
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
- Correction detection lives in Tracking's `SetValueFromOrigin` (Task 11) and the outcome it consumes is recorded by the existing `PropertyValueEqualityCheckHandler` itself, so PR 2 adds no new write interceptor, needs no `[RunsFirst]`/`[RunsBefore]` ordering, touches no core file, and needs no Connectors registration; the spec's PR 2 Detection section matches this.
- Type consistency: `ChangeOrigin.Correction(object)`, `PendingOrigin.Set(PropertyReference, ChangeOrigin, object?)`, `SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?)`, `PropertyValidationContext<TProperty>(PropertyReference, TProperty, ChangeOrigin)` used identically across tasks.
- Pending-origin reach: Connectors cannot call the internal `PendingOrigin.Set` (core's `InternalsVisibleTo` excludes Connectors), so the appliers set the pending origin through the public `SetValueFromOrigin` for `FromSource`/`Confirmed` and keep the unstamped path for `Local`.
