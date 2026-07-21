# Expression-Based Path Subscriptions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `SubscribeToPath`, an expression-based subscription that observes "the value at `x.Foo.Bar[3].Baz`" as the path itself moves over time, by composing the merged per-property change subscription primitive (PR #377) with zero new core API.

**Architecture:** Decompose a chained lambda into an ordered segment list, install one per-property primitive subscription per segment along the currently resolved path, and re-subscribe only the suffix when an intermediate segment changes. Every processed callback runs a fresh bounds-checked walk from the root that both computes the observed state and validates the subscribed chain, retracking (subscribe-before-read) from the first divergence. Delivery is synchronous on the writing thread through an exclusive-drain queue outside a small per-subscription lock. `Current` is a pure, lock-free walk. Lives in a new `Paths/` folder in `Namotion.Interceptor.Tracking` with no Registry dependency.

**Tech Stack:** C# 13 / .NET 9.0, `System.Threading.Lock`, `System.Linq.Expressions` (decomposition + compiled accessors), the merged `PropertyChangeSubscription` primitive, xUnit + Verify + PublicApiGenerator, BenchmarkDotNet.

## Global Constraints

- **Target package:** `Namotion.Interceptor.Tracking`, new folder `Paths/`, namespace `Namotion.Interceptor.Tracking.Paths`. Must compile with **no `Namotion.Interceptor.Registry` reference**.
- **No new core API:** nothing added to `Namotion.Interceptor` (the core snapshot is unchanged). Only the Tracking public snapshot grows.
- **Composes, does not modify the primitive:** segment installs use the internal non-validating `PropertyChangeSubscription.Create(PropertyReference, IPropertyChangeObserver)` and non-throwing `subject.Properties.TryGetValue`; never the public `Subscribe`/`SubscribeToProperty` (those validate and throw into a writer's chain). Public `SubscribeToPath` validates only at its own boundary.
- **Nullable enabled, warnings as errors** (`Directory.Build.props`). `netstandard2.0` is NOT a target here (Tracking is net9.0), so `System.Threading.Lock`, `ImmutableArray`, and spans are available.
- **One release-safe PR:** public surface + tracker + tests + benchmarks + docs land together; no public API ahead of consumers.
- **Test discipline (mandatory):** every test file that creates a path or per-property subscription, or asserts on the process-wide count / idle gate, MUST declare `[Collection(PerPropertySubscriptionCollection.Name)]` and call `PropertyChangeSubscriptions.ResetForTests()` in its constructor. `PerPropertySubscriptionConventionsTests` already lists `SubscribeToPath` as a sensitive marker and will FAIL the build if a `SubscribeToPath` test file omits the collection attribute.
- **Naming (settled, do not bikeshed):** `SubscribeToPath`, `SubjectPathSubscription<TValue>`, `SubjectPathValue<TValue>`, `SubjectPathChange<TValue>`, `SubjectPathChangeKind`, `SubjectPathChangeCallback<TValue>`, `ISubjectPathChangeObserver<TValue>`, `IsResolved`.
- **Style:** no abbreviations (`attribute` not `attr`), no em dashes in docs/PRs, minimal comments (explain only the non-obvious). Test convention: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange` / `// Act` / `// Assert` (`// Act & Assert` for throw tests), no hardcoded `Task.Delay`/`Thread.Sleep` (use `ManualResetEventSlim`/`CountdownEvent`/`AsyncTestHelpers`).
- **No AI attribution** in commits or PR text.
- **Build:** `dotnet build src/Namotion.Interceptor.slnx`. **Unit tests:** `dotnet test src/Namotion.Interceptor.Tracking.Tests`. **Snapshot loops:** prefix `DiffEngine_Disabled=true`. **Benchmarks:** `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release`, PR gated by the `requires-benchmark` label.

---

## File Structure

**Production (all under `src/Namotion.Interceptor.Tracking/Paths/`):**

- `SubjectPathChangeKind.cs` - the `ValueChange`/`PathChange` enum (public).
- `SubjectPathValue.cs` - `SubjectPathValue<TValue>` readonly struct (public) plus an internal equality helper for suppression.
- `SubjectPathChange.cs` - `SubjectPathChange<TValue>` readonly struct (public).
- `SubjectPathChangeCallback.cs` - the delegate + `ISubjectPathChangeObserver<TValue>` interface (public).
- `PathSegment.cs` - internal ordered-segment model (property name + optional fixed index/key selector + static shape metadata).
- `PathExpressionDecomposer.cs` - internal static: lambda → `PathSegment[]`, static-tier validation, index/key single evaluation.
- `PathValueAccessors.cs` - internal static: compiled typed leaf accessor cache and the value-typed (`ImmutableArray<T>`) read-and-index accessor cache and typed dictionary `TryGetValue` accessor cache; lenient bounds-checked collection/dictionary lookups.
- `PathWalker.cs` - internal static: the lenient, never-throwing validating walk producing `SubjectPathValue<TValue>` and the resolved-subject chain.
- `SubjectPathSubscription.cs` - `SubjectPathSubscription<TValue>` (public handle) containing the tracker: chain build/retrack (subscribe-before-read), per-segment observer, slot-identity guard, event computation, drain queue + direct-dispatch, reentrancy guard, transaction suppression, `Current`, `Dispose`.
- `SubjectPathSubscriptionExtensions.cs` - the two public `SubscribeToPath` extension methods (callback + observer overloads), null guards, transaction subscribe-guard, teardown-on-throw.

**Tests (all under `src/Namotion.Interceptor.Tracking.Tests/Paths/`):**

- `PathSubscriptionCollection.cs` is NOT created; reuse the existing `PerPropertySubscriptionCollection` (its `Name` is referenced by every path test file).
- `PathModels.cs` - extra `[InterceptorSubject]` test models the existing `Person`/`Car`/`Garage`/`Tire` do not cover (interface intermediate, `[Derived]` interface-default leaf, derived subject-typed intermediate, `int`-keyed dictionary, `List<T>`/`IList<T>` of subjects, `int?` leaf, a non-`IEquatable` struct leaf, a hostile custom container, a case-insensitive generic-only dictionary).
- `PathExpressionValidationTests.cs`, `PathCurrentTests.cs`, `PathTransitionTests.cs`, `PathChainInvariantTests.cs`, `PathPropertyTypeMatrixTests.cs`, `PathConcurrencyTests.cs`, `PathDormancyTests.cs`, `PathTransactionTests.cs`, `PathDisposeTests.cs`, `PathCauseTests.cs` - one file per spec test cluster.

**Benchmarks:** `src/Namotion.Interceptor.Benchmark/SubjectPathSubscriptionBenchmark.cs` (+ registration in `Program.cs`).

**Docs:** new "Path Subscriptions" section in `docs/tracking.md` after "Per-Property Subscriptions".

**Snapshot:** `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt` regenerated to include the new `Namotion.Interceptor.Tracking.Paths` public types.

---

## Phase 1: Public value-type surface

Pure data types with no dependencies on the tracker. Landable and unit-testable in isolation.

### Task 1: `SubjectPathChangeKind` and `SubjectPathValue<TValue>`

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathChangeKind.cs`
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathValue.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathValueTests.cs`

**Interfaces:**
- Produces: `enum SubjectPathChangeKind { ValueChange, PathChange }`; `readonly struct SubjectPathValue<TValue>` with `bool IsResolved`, `TValue? Value`, `bool TryGetValue([MaybeNull] out TValue)`, `TValue? GetValueOrDefault()`, `TValue GetValueOrDefault(TValue fallback)`, static factory `Unresolved`/`Resolved(TValue?)`, and internal static `bool AreEquivalent(in SubjectPathValue<TValue> a, in SubjectPathValue<TValue> b)` used later for suppression.

- [ ] **Step 1: Write the failing test** (`PathValueTests.cs`)

```csharp
using Namotion.Interceptor.Tracking.Paths;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

public class PathValueTests
{
    [Fact]
    public void WhenUnresolved_ThenTryGetValueIsFalseAndDefaultsReturned()
    {
        // Arrange
        var value = SubjectPathValue<string>.Unresolved;

        // Act
        var got = value.TryGetValue(out var inner);

        // Assert
        Assert.False(value.IsResolved);
        Assert.False(got);
        Assert.Null(inner);
        Assert.Null(value.GetValueOrDefault());
        Assert.Equal("fb", value.GetValueOrDefault("fb"));
    }

    [Fact]
    public void WhenResolvedNull_ThenTryGetValueIsTrueWithNull()
    {
        // Arrange
        var value = SubjectPathValue<string?>.Resolved(null);

        // Act
        var got = value.TryGetValue(out var inner);

        // Assert
        Assert.True(value.IsResolved);
        Assert.True(got);
        Assert.Null(inner);
        Assert.Null(value.GetValueOrDefault());      // resolved but null
        Assert.Equal("fb", value.GetValueOrDefault("fb")); // fallback on resolved null
    }

    [Fact]
    public void WhenResolvedNonNull_ThenValueReturnedAndFallbackIgnored()
    {
        // Arrange
        var value = SubjectPathValue<int>.Resolved(7);

        // Act & Assert
        Assert.True(value.TryGetValue(out var inner));
        Assert.Equal(7, inner);
        Assert.Equal(7, value.GetValueOrDefault());
        Assert.Equal(7, value.GetValueOrDefault(99));
    }

    [Fact]
    public void WhenComparedForSuppression_ThenResolvednessAndValueDecideEquivalence()
    {
        // Arrange & Act & Assert
        Assert.True(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Unresolved, SubjectPathValue<int>.Unresolved));
        Assert.False(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Unresolved, SubjectPathValue<int>.Resolved(0)));
        Assert.True(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Resolved(5), SubjectPathValue<int>.Resolved(5)));
        Assert.False(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Resolved(5), SubjectPathValue<int>.Resolved(6)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathValueTests"`
Expected: FAIL to compile ("SubjectPathValue does not exist").

- [ ] **Step 3: Write minimal implementation**

`SubjectPathChangeKind.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>The kind of observable transition delivered by a path subscription.</summary>
public enum SubjectPathChangeKind
{
    /// <summary>The current resolved leaf itself was written while the chain was intact; <see cref="SubjectPathChange{TValue}.Cause"/> is that leaf write.</summary>
    ValueChange,

    /// <summary>The observed state changed for any other reason (a structural write, or a revalidation triggered by an off-path write after a dormant divergence).</summary>
    PathChange
}
```

`SubjectPathValue.cs`:

```csharp
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>One observed state of a watched path: either resolved (every segment reachable, the leaf property exists) with a value that may itself be null, or unresolved.</summary>
public readonly struct SubjectPathValue<TValue>
{
    private readonly TValue? _value;

    private SubjectPathValue(bool isResolved, TValue? value)
    {
        IsResolved = isResolved;
        _value = value;
    }

    /// <summary>Every segment reachable and indices in range, so the leaf property exists; its value may still be null.</summary>
    public bool IsResolved { get; }

    /// <summary>The leaf's value; <c>default</c> when not resolved.</summary>
    public TValue? Value => _value;

    /// <summary>The unresolved state (also <c>default</c>).</summary>
    public static SubjectPathValue<TValue> Unresolved => default;

    /// <summary>A resolved state carrying the leaf value (which may be null).</summary>
    public static SubjectPathValue<TValue> Resolved(TValue? value) => new(true, value);

    /// <summary>False when unresolved; true with a possibly-null value for a resolved leaf.</summary>
    public bool TryGetValue([MaybeNull] out TValue value)
    {
        value = _value;
        return IsResolved;
    }

    /// <summary><c>default(TValue)</c> when unresolved; the leaf value (may be null) when resolved.</summary>
    public TValue? GetValueOrDefault() => IsResolved ? _value : default;

    /// <summary><paramref name="fallback"/> when unresolved OR the resolved value is null; the resolved non-null value otherwise.</summary>
    public TValue GetValueOrDefault(TValue fallback) => IsResolved && _value is not null ? _value : fallback;

    /// <summary>Suppression equivalence: same resolvedness and, when both resolved, values equal under <see cref="EqualityComparer{TValue}.Default"/>.</summary>
    internal static bool AreEquivalent(in SubjectPathValue<TValue> a, in SubjectPathValue<TValue> b)
    {
        if (a.IsResolved != b.IsResolved)
        {
            return false;
        }

        return !a.IsResolved || EqualityComparer<TValue>.Default.Equals(a._value, b._value);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathValueTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths src/Namotion.Interceptor.Tracking.Tests/Paths
git commit -m "feat(paths): add SubjectPathValue and SubjectPathChangeKind"
```

### Task 2: `SubjectPathChange<TValue>`, callback delegate, observer interface

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathChange.cs`
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathChangeCallback.cs`
- Test: covered indirectly (compile-only); no dedicated behavior test (these are DTO/delegate declarations).

**Interfaces:**
- Consumes: `SubjectPathValue<TValue>` (Task 1), `SubjectPropertyChange` (from `Namotion.Interceptor.Tracking.Change`).
- Produces: `readonly struct SubjectPathChange<TValue>` with `SubjectPathChangeKind Kind`, `SubjectPathValue<TValue> Old`, `SubjectPathValue<TValue> New`, `SubjectPropertyChange Cause`, and an `internal` constructor `(SubjectPathChangeKind, SubjectPathValue<TValue> old, SubjectPathValue<TValue> @new, in SubjectPropertyChange cause)`; `delegate void SubjectPathChangeCallback<TValue>(in SubjectPathChange<TValue> change)`; `interface ISubjectPathChangeObserver<TValue> { void OnChange(in SubjectPathChange<TValue> change); }`.

- [ ] **Step 1: Write the implementation** (no separate test; verified by Phase 4 delivery tests)

`SubjectPathChange.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>An observable transition of a watched path. <see cref="Cause"/> is the real property write that triggered it (supplementary provenance, not something a consumer must decode).</summary>
public readonly struct SubjectPathChange<TValue>
{
    internal SubjectPathChange(
        SubjectPathChangeKind kind,
        SubjectPathValue<TValue> old,
        SubjectPathValue<TValue> @new,
        in SubjectPropertyChange cause)
    {
        Kind = kind;
        Old = old;
        New = @new;
        Cause = cause;
    }

    public SubjectPathChangeKind Kind { get; }

    /// <summary>Observed state before this event.</summary>
    public SubjectPathValue<TValue> Old { get; }

    /// <summary>Observed state now (a fresh walk, or after a divergent retrack the retrack's reads; never copied from the causing write payload).</summary>
    public SubjectPathValue<TValue> New { get; }

    /// <summary>The real write that triggered this event. <c>Cause.Origin</c> is the trigger's origin verbatim and is deliberately not provenance for <see cref="New"/>.</summary>
    public SubjectPropertyChange Cause { get; }
}
```

`SubjectPathChangeCallback.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>Delegate form of <see cref="ISubjectPathChangeObserver{TValue}"/>. Must be fast, non-blocking, and must not throw.</summary>
public delegate void SubjectPathChangeCallback<TValue>(in SubjectPathChange<TValue> change);

/// <summary>Zero-closure observer for a path subscription; mirrors <c>IPropertyChangeObserver</c>. Implementations must be fast, non-blocking, and must not throw.</summary>
public interface ISubjectPathChangeObserver<TValue>
{
    void OnChange(in SubjectPathChange<TValue> change);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths
git commit -m "feat(paths): add SubjectPathChange, callback delegate and observer interface"
```

---

## Phase 2: Expression decomposition and static validation

### Task 3: `PathSegment` model

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/PathSegment.cs`

**Interfaces:**
- Produces: `internal enum PathSegmentKind { Property, CollectionIndex, DictionaryKey }`; `internal sealed class PathSegment` with `string PropertyName`, `PathSegmentKind Kind`, `int CollectionIndex`, `object? DictionaryKey`, `bool IsLeaf`, `Type PropertyStaticType` (the property's declared type from the expression), `Type? DictionaryInterfaceType` (closed `IDictionary<,>` / `IReadOnlyDictionary<,>` for the typed accessor, null unless `Kind == DictionaryKey`), `bool IsValueTypedCollection` (true only for an `ImmutableArray<T>` collection segment). The list of segments is the tracker's internal contract; each segment is one subscribed property, indexed where applicable.

- [ ] **Step 1: Write the implementation**

```csharp
using System;

namespace Namotion.Interceptor.Tracking.Paths;

internal enum PathSegmentKind
{
    Property,
    CollectionIndex,
    DictionaryKey
}

/// <summary>
/// One node of a decomposed path: a single subscribed property name plus an optional fixed
/// collection index or dictionary key evaluated once at subscribe time. Resolution is by name
/// against the runtime subject; the static-type fields drive accessor construction and validation only.
/// </summary>
internal sealed class PathSegment
{
    public required string PropertyName { get; init; }
    public required PathSegmentKind Kind { get; init; }
    public required Type PropertyStaticType { get; init; }
    public bool IsLeaf { get; init; }

    // Valid when Kind == CollectionIndex.
    public int CollectionIndex { get; init; }
    // True only when the collection segment's static type is ImmutableArray<T> (a value type that
    // would box through the object-returning metadata getter).
    public bool IsValueTypedCollection { get; init; }

    // Valid when Kind == DictionaryKey.
    public object? DictionaryKey { get; init; }
    public Type? DictionaryInterfaceType { get; init; }
    public Type? DictionaryKeyType { get; init; }
    public Type? DictionaryValueType { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths/PathSegment.cs
git commit -m "feat(paths): add internal PathSegment model"
```

### Task 4: `PathExpressionDecomposer` - decomposition + static-tier validation

Static expression-shape defects always throw at subscribe (they can never become valid). Runtime-validity defects are handled later by the walk (Phase 3/4). Index/key argument expressions are evaluated exactly once here.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/PathExpressionDecomposer.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathExpressionValidationTests.cs`
- Test model additions: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathModels.cs` (created here; grown in later phases)

**Interfaces:**
- Consumes: `PathSegment`, `PathSegmentKind`, `SubjectPropertyTypeExtensions.IsSubjectReferenceType/IsSubjectCollectionType/IsSubjectDictionaryType`.
- Produces: `internal static PathSegment[] Decompose<TSubject, TValue>(Expression<Func<TSubject, TValue>> path)`. Throws `ArgumentException` (with a clear message naming the offending shape) for every static-tier defect. Returns the ordered segment array otherwise, with `IsLeaf == true` on the last element.

**Static-tier defects that MUST throw here (spec "Validation boundary", tier 1):** cast (any `Convert`/`ConvertChecked` except the compiler's boxing convert on the leaf, which is unwrapped), a member that is not a `PropertyInfo` (field selector), a method call other than a single-argument indexer `get_Item` (multi-argument indexer such as `m.Grid[1,2]` rejected), captured-object chain (`m => other.Speed`, i.e. the head is not the lambda parameter), a static member, an index argument that references the lambda parameter (`x => x.Items[x.Index]`), an index argument (constant or captured) evaluating to a null dictionary key or a negative collection index, a path ending in an indexed element (`x => x.Items[3]` with no trailing property), the identity path (`x => x`), a `get_Item` indexer on a property that is not a subject collection or dictionary, a nested indexer whose receiver is another indexer (`x => x.Grid[1][2].Name`), and a non-subject intermediate (a `.Foo` chained further whose static type is neither `IInterceptorSubject`-assignable nor a subject collection/dictionary element type).

- [ ] **Step 1: Write the failing tests** (`PathModels.cs` then `PathExpressionValidationTests.cs`)

`PathModels.cs` (initial models needed for validation tests; extended later):

```csharp
using System.Collections.Generic;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[InterceptorSubject]
public partial class Node
{
    public Node() { Name = string.Empty; Children = []; }

    public partial string Name { get; set; }
    public partial Node? Child { get; set; }
    public partial Node[] Children { get; set; }
    public partial Dictionary<string, Node> ByName { get; set; } = new();
    public int PlainField;                 // not a property
    public int Index { get; set; }         // used to build an invalid index-arg expression
}
```

`PathExpressionValidationTests.cs`:

```csharp
using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Tracking.Paths;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// SubscribeToPath is a sensitive marker; this file must join the serialized collection even though
// the decomposer tests do not create subscriptions (the conventions test scans for the marker text).
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathExpressionValidationTests
{
    private static void Decompose<TValue>(Expression<Func<Node, TValue>> path)
        => PathExpressionDecomposer.Decompose(path);

    [Fact]
    public void WhenIdentityPath_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose<Node>(x => x));
    }

    [Fact]
    public void WhenFieldSelector_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.PlainField));
    }

    [Fact]
    public void WhenPathEndsInIndexedElement_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[3]));
    }

    [Fact]
    public void WhenIndexArgumentReferencesLambdaParameter_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[x.Index].Name));
    }

    [Fact]
    public void WhenNegativeCollectionIndex_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[-1].Name));
    }

    [Fact]
    public void WhenValidChainedPath_ThenSegmentsAreOrderedWithLeafLast()
    {
        // Act
        var segments = PathExpressionDecomposer.Decompose<Node, string>(x => x.Child!.Children[2].Name);

        // Assert
        Assert.Equal(3, segments.Length);
        Assert.Equal("Child", segments[0].PropertyName);
        Assert.Equal(PathSegmentKind.Property, segments[0].Kind);
        Assert.Equal("Children", segments[1].PropertyName);
        Assert.Equal(PathSegmentKind.CollectionIndex, segments[1].Kind);
        Assert.Equal(2, segments[1].CollectionIndex);
        Assert.Equal("Name", segments[2].PropertyName);
        Assert.True(segments[2].IsLeaf);
    }

    [Fact]
    public void WhenIndexIsCapturedVariable_ThenEvaluatedOnceAtDecompose()
    {
        // Arrange
        var i = 1;

        // Act
        var segments = PathExpressionDecomposer.Decompose<Node, string>(x => x.Children[i].Name);
        i = 5; // must not change the already-decomposed index

        // Assert
        Assert.Equal(1, segments[1].CollectionIndex);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathExpressionValidationTests"`
Expected: FAIL to compile ("PathExpressionDecomposer does not exist").

- [ ] **Step 3: Write the implementation** (`PathExpressionDecomposer.cs`)

Algorithm:
1. Unwrap a single boxing `Convert`/`ConvertChecked` on the body only (the leaf-to-`object` box); a convert anywhere else throws.
2. Walk the expression tree from the outermost member/index down to the parameter, pushing each accepted segment onto a stack; reverse at the end so the leaf is last.
3. For a `MemberExpression`: require `member.Member is PropertyInfo`; record a `Property` segment (name = property name, `PropertyStaticType` = `property.PropertyType`). The next receiver is `member.Expression`.
4. For a `MethodCallExpression`: require the method is a single-argument `get_Item` on a receiver that is itself a `MemberExpression` whose member is a subject collection/dictionary property (reject a `get_Item` on an indexer result - nested indexer - and on a non-collection/non-dictionary property, and reject multi-arg indexers). Decide collection vs dictionary from the receiver property's static type via `SubjectPropertyTypeExtensions`. Evaluate the single index/key argument via `Expression.Lambda(arg).Compile()()` - but first reject if the argument references the lambda parameter (walk the arg for a `ParameterExpression` equal to `path.Parameters[0]`). For a collection: require an `int`, reject negative. For a dictionary: reject null. Set `PropertyStaticType` to the receiver property type, `IsValueTypedCollection` true only when the receiver property type is a closed `ImmutableArray<T>`, and for a dictionary set `DictionaryInterfaceType`/`DictionaryKeyType`/`DictionaryValueType` from the closed `IDictionary<,>`/`IReadOnlyDictionary<,>` of the receiver type (prefer the exact declared interface). The *segment produced is on the collection/dictionary property* (one subscribed property, indexed); the receiver `MemberExpression` is consumed by this segment, not emitted separately. The next receiver is the receiver member's own `.Expression`.
5. Base case: the receiver is `path.Parameters[0]` → stop. Anything else at the head (a captured object, a static access, a cast) throws.
6. After building, require at least one segment (else identity path throws) and that the last segment is a member `Property` segment (a path ending in an index throws - enforced because an index segment is never marked `IsLeaf` unless followed by a property, so a trailing index produces a segment whose `IsLeaf` cannot be set; detect by "the outermost node was a `get_Item`" and throw).
7. Static intermediate check: every non-leaf segment's effective element/property type must be subject-typed. For a `Property` intermediate, `PropertyStaticType.IsSubjectReferenceType()` (includes interfaces extending `IInterceptorSubject`). For an index/key intermediate, the collection element type / dictionary value type must implement `IInterceptorSubject` or be a subject-capable interface. Reject otherwise with a clear message.
8. Mark the last segment `IsLeaf = true`.

Provide clear `ArgumentException` messages naming the offending shape (mirror the primitive's `ResolveDirectPropertyName` message tone).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathExpressionValidationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths/PathExpressionDecomposer.cs src/Namotion.Interceptor.Tracking.Tests/Paths
git commit -m "feat(paths): decompose path expressions with static-tier validation"
```

### Task 5: Remaining static-validation cases (method call, captured chain, multi-arg indexer, nested indexer, non-subject intermediate, custom indexer)

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/PathExpressionDecomposer.cs` (harden the rejections)
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathExpressionValidationTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathModels.cs` (add a `Grid` multi-dim indexer holder, a custom-indexer subject, a non-subject `.Foo` chain target)

**Interfaces:** unchanged (`Decompose` still throws `ArgumentException`).

- [ ] **Step 1: Add models** to `PathModels.cs`

```csharp
[InterceptorSubject]
public partial class GridHolder
{
    public GridHolder() { Grid = new Node[0, 0]; Rows = []; }
    public partial Node[,] Grid { get; set; }           // multi-dim indexer
    public partial List<List<Node>> Rows { get; set; }  // nested indexer receiver
    public partial int Number { get; set; }             // non-subject intermediate target
}
```

- [ ] **Step 2: Write failing tests**

```csharp
[Fact]
public void WhenMultiArgumentIndexer_ThenThrows()
{
    // Act & Assert
    Assert.Throws<ArgumentException>(() =>
        PathExpressionDecomposer.Decompose<GridHolder, string>(x => x.Grid[1, 2].Name));
}

[Fact]
public void WhenNestedIndexerReceiverIsAnotherIndexer_ThenThrows()
{
    // Act & Assert
    Assert.Throws<ArgumentException>(() =>
        PathExpressionDecomposer.Decompose<GridHolder, string>(x => x.Rows[1][2].Name));
}

[Fact]
public void WhenNonSubjectIntermediate_ThenThrows()
{
    // Act & Assert: Number is an int, cannot be an intermediate.
    Assert.Throws<ArgumentException>(() =>
        PathExpressionDecomposer.Decompose<GridHolder, int>(x => x.Number.GetHashCode()));
}

[Fact]
public void WhenCapturedObjectChain_ThenThrows()
{
    // Arrange
    var other = new Node();

    // Act & Assert
    Assert.Throws<ArgumentException>(() =>
        PathExpressionDecomposer.Decompose<Node, string>(x => other.Name));
}
```

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathExpressionValidationTests"` - expect the new four to FAIL (or the build to fail on the `Grid[1,2]` shape) until the rejections are hardened.

- [ ] **Step 3: Harden the decomposer** so `Grid[1,2]` (a two-arg `get_Item`), `Rows[1][2]` (indexer receiver), `other.Name` (head not the parameter), and a `.Foo.Bar()` non-indexer method call all throw with clear messages.

- [ ] **Step 4: Run tests** - Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): reject multi-arg, nested and captured indexer shapes"
```

---

## Phase 3: The lenient walk, typed accessors, and `Current`

### Task 6: `PathValueAccessors` - typed leaf, value-typed collection, dictionary accessors, lenient lookups

Everything read during a walk goes through the intercepted getters (so reads honor the subject `SyncRoot`, the derived read hook, and - for `Current` - an ambient transaction's staged view). The typed accessors exist to avoid boxing value-typed reads, keeping the whole walk allocation-free.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/PathValueAccessors.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathAccessorTests.cs`

**Interfaces:**
- Consumes: `SubjectPropertyMetadata` (`GetValue`, `PropertyInfo`, `IsDerived`, `IsIntercepted`), `SubjectLookup` (as precedent only), `System.Collections.Immutable`.
- Produces (all `internal static`):
  - `Func<IInterceptorSubject, TValue> GetLeafAccessor<TValue>(Type declaringType, PropertyInfo propertyInfo)` - compiled once per `(declaringType, name, typeof(TValue))`, invokes the property getter typed (routes through interception, no boxing). Used only when `propertyInfo.PropertyType` is assignable to `TValue`.
  - `Func<IInterceptorSubject, int, IInterceptorSubject?> GetImmutableArrayIndexer(Type declaringType, PropertyInfo propertyInfo, Type elementType)` - compiled once per `(declaringType, name)`; reads the `ImmutableArray<T>` typed (no box), returns null when default or the index is out of range, else the element as `IInterceptorSubject?`. This is the read-and-index accessor for value-typed collection intermediates (see Blocking assumptions: the spec's literal `Func<IInterceptorSubject, TSegment>` cannot avoid boxing for a value-typed intermediate the generic tracker does not know statically; a read-and-index delegate returning the reference-typed child does).
  - `Func<IInterceptorSubject, object, IInterceptorSubject?> GetDictionaryLookup(Type declaringType, PropertyInfo propertyInfo, PathSegment segment)` - compiled once per `(declaringType, name)`; casts the read dictionary value to the declared `IDictionary<TKey,TValue>`/`IReadOnlyDictionary<TKey,TValue>` and calls `TryGetValue` (honoring a custom comparer), returning the value as `IInterceptorSubject?` or null on a missing key. Wrapped so a throwing `Count`/indexer/`TryGetValue`/comparer surfaces to the walk (walk catches).
  - `IInterceptorSubject? IndexReferenceCollection(object collection, int index)` - a lenient variant of `SubjectLookup.FindSubjectInCollection` that checks `Count` before indexing (`IList` fast path guarded) and treats out-of-range / wrong-shaped slot as null, never throwing (`List<T>`, `T[]`, boxed `ImmutableArray<T>` default all handled). Used for reference-typed collection intermediates.

- [ ] **Step 1: Write failing tests** (`PathAccessorTests.cs`) covering: typed leaf read of a value type returns the value without boxing (assert value only; the allocation gate is Phase 8), `GetImmutableArrayIndexer` returns null for a default `ImmutableArray` and for out-of-range and the element otherwise, `IndexReferenceCollection` returns null for out-of-range on `List<T>`/`T[]` instead of throwing, and `GetDictionaryLookup` returns the value for a present key and null for a missing key.

```csharp
using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathAccessorTests
{
    [Fact]
    public void WhenImmutableArrayIsDefault_ThenIndexerReturnsNullNotThrow()
    {
        // Arrange
        var garage = new Garage();
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.SpareTires))!;
        var indexer = PathValueAccessors.GetImmutableArrayIndexer(typeof(Garage), propertyInfo, typeof(Tire));
        garage.SpareTires = default; // uninitialized ImmutableArray

        // Act
        var result = indexer(garage, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenReferenceCollectionIndexOutOfRange_ThenReturnsNullNotThrow()
    {
        // Arrange
        var car = new Car(); // 4 tires

        // Act
        var result = PathValueAccessors.IndexReferenceCollection(car.Tires, 99);

        // Assert
        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run to verify they fail** - Expected: compile failure.

- [ ] **Step 3: Implement `PathValueAccessors`** with `ConcurrentDictionary` caches keyed by `(Type declaringType, string name, Type? segmentType)`, compiled expression-tree delegates that invoke the property getter (`Expression.Property(Expression.Convert(param, declaringType), propertyInfo)`), and the lenient container helpers. Model the compiled-delegate caches on `SubjectLookup`.

- [ ] **Step 4: Run to verify they pass**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths/PathValueAccessors.cs src/Namotion.Interceptor.Tracking.Tests/Paths/PathAccessorTests.cs
git commit -m "feat(paths): typed and lenient path value accessors"
```

### Task 7: `PathWalker` - lenient validating walk

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/PathWalker.cs`
- Test: exercised by `PathCurrentTests` (Task 8) and later retrack tests.

**Interfaces:**
- Consumes: `PathSegment`, `PathValueAccessors`, `subject.Properties.TryGetValue`, `metadata.GetValue`, `metadata.IsIntercepted`, `metadata.IsDerived`, `metadata.PropertyInfo`.
- Produces: `internal static SubjectPathValue<TValue> Walk<TValue>(PathSegment[] segments, IInterceptorSubject root, IInterceptorSubject?[] resolvedSubjects)`. `resolvedSubjects[i]` is set to the subject that segment `i` is read on (`resolvedSubjects[0] = root`); entries beyond the resolved prefix are set to null. Returns the leaf `SubjectPathValue<TValue>` (resolved or unresolved). **Never throws**: a missing property, a segment failing `IsIntercepted || IsDerived`, any getter or container operation that throws, an out-of-range index, a missing key, a present-but-null value, a wrong-shaped slot, or a leaf value not assignable to `TValue` all resolve to unresolved from that segment. Wrap every traversal operation (property read, `Count`, indexer, `TryGetValue`, comparer) in the resolve-to-unresolved boundary.

Walk algorithm per segment `i` on `current` subject:
1. `current` null → unresolved (short prefix).
2. `current.Properties.TryGetValue(segment.PropertyName, out metadata)` false → unresolved.
3. `!(metadata.IsIntercepted || metadata.IsDerived)` → unresolved.
4. If leaf: read the typed value. If `metadata.PropertyInfo` is null (dynamic) or its type is not assignable to `TValue`, fall back to `metadata.GetValue(current)` with a guarded `is TValue` cast; on mismatch → unresolved. Return `Resolved(value)`.
5. Intermediate `Property`: read child via `metadata.GetValue(current)` (reference, no box), cast to `IInterceptorSubject?`; null → unresolved; else set `resolvedSubjects[i+1]` and continue.
6. Intermediate `CollectionIndex`: if `IsValueTypedCollection`, use `GetImmutableArrayIndexer` (bounds-checked, returns child or null); else `metadata.GetValue(current)` then `PathValueAccessors.IndexReferenceCollection`. Null → unresolved; else set next subject.
7. Intermediate `DictionaryKey`: `GetDictionaryLookup(...)(current, key)`; null → unresolved; else set next subject.

- [ ] **Step 1: Implement `PathWalker.Walk<TValue>`** wrapping each step so no exception escapes (a `try/catch` boundary around the getter and container operations, returning unresolved).

- [ ] **Step 2: Build** - Expected: succeeds. (Behavior verified in Task 8.)

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths/PathWalker.cs
git commit -m "feat(paths): lenient never-throwing validating walk"
```

### Task 8: `SubjectPathSubscription<TValue>.Current` + `SubscribeToPath` boundary (Current-only, no delivery yet)

Deliver a usable `SubscribeToPath` that validates, decomposes, exposes `Current`, and disposes cleanly - but does NOT yet install segment subscriptions or deliver events (Phase 4). This isolates the pure-read surface and its tests.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs` (skeleton: segments + `Current` + `Dispose` no-op-ish)
- Create: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscriptionExtensions.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathCurrentTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class SubjectPathSubscription<TValue> : IDisposable` with `public SubjectPathValue<TValue> Current { get; }` (fresh, tracker-lock-free walk from the root; returns unresolved default after dispose) and `public void Dispose()`. Internal ctor takes `(IInterceptorSubject root, PathSegment[] segments)`.
  - `public static class SubjectPathSubscriptionExtensions` with the two overloads:
    - `SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(this TSubject subject, Expression<Func<TSubject, TValue>> path, SubjectPathChangeCallback<TValue> callback) where TSubject : IInterceptorSubject`
    - `SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(this TSubject subject, Expression<Func<TSubject, TValue>> path, ISubjectPathChangeObserver<TValue> observer) where TSubject : IInterceptorSubject`
  - Both: `ArgumentNullException` for null subject/path/callback/observer; `InvalidOperationException` when called on a flow with an active non-committing transaction (`SubjectTransaction.Current is { IsCommitting: false }`); then `PathExpressionDecomposer.Decompose`. In this task the constructor only stores the root and segments; Phase 4 adds the chain build and delivery.

- [ ] **Step 1: Write failing tests** (`PathCurrentTests.cs`): initial `Current` for a resolved path, an unresolved path (null intermediate), an out-of-range index (never throws) on the `IList` fast-path types, a resolved-null leaf, and a dictionary present-key-null-value resolving as unresolved. Plus null-argument and transaction-subscribe-guard throws.

```csharp
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathCurrentTests
{
    public PathCurrentTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenPathResolves_ThenCurrentHasLeafValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { });

        // Act
        var current = subscription.Current;

        // Assert
        Assert.True(current.IsResolved);
        Assert.Equal("Joe", current.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntermediateNull_ThenCurrentIsUnresolved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father is null
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { });

        // Act & Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenIndexOutOfRange_ThenCurrentIsUnresolvedNotThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Children is empty
        using var subscription = person.SubscribeToPath(x => x.Children[3].FirstName, (in SubjectPathChange<string?> _) => { });

        // Act & Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenSubscribeArgumentsNull_ThenThrowArgumentNullException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToPath(x => x.FirstName, (in SubjectPathChange<string?> _) => { }));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath((System.Linq.Expressions.Expression<Func<Person, string?>>)null!, (in SubjectPathChange<string?> _) => { }));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath(x => x.FirstName, (SubjectPathChangeCallback<string?>)null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath(x => x.FirstName, (ISubjectPathChangeObserver<string?>)null!));
    }
}
```

Add a transaction-guard test in `PathTransactionTests.cs` (Task 15) for the `InvalidOperationException`; a stub here is acceptable but keep the full version with Phase 5.

- [ ] **Step 2: Run to verify they fail** - Expected: compile failure.

- [ ] **Step 3: Implement** the extension methods (null guards → transaction guard → decompose → construct) and `SubjectPathSubscription<TValue>` with `Current` calling `PathWalker.Walk<TValue>(_segments, _root, rentedArray)` (or a stack-allocated `IInterceptorSubject?[_segments.Length]`), returning unresolved default when `_disposed`. `Dispose` sets the disposed flag (one-shot). Delegate observer wrapper mirrors the primitive's `DelegateObserver`.

- [ ] **Step 4: Run to verify they pass**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Paths src/Namotion.Interceptor.Tracking.Tests/Paths/PathCurrentTests.cs
git commit -m "feat(paths): SubscribeToPath boundary and lock-free Current"
```

---

## Phase 4: The tracker - chain, delivery, guards

This is the core. Split into tasks that each end at an independently testable deliverable. All work lives in `SubjectPathSubscription.cs`; the per-tracker lock is a `System.Threading.Lock`.

### Task 9: Segment observer, subscribe-before-read chain build, slot-identity guard, `_lastObserved` seed

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathTransitionTests.cs` (first delivery tests)

**Interfaces:**
- Consumes: `PropertyChangeSubscription.Create(PropertyReference, IPropertyChangeObserver)` (returns `IDisposable`), `SubjectPropertyChange`, `PropertyChangeSubscriptions` count (via the primitive; not directly).
- Produces (internal within the class):
  - a nested `sealed class PathSegmentObserver : IPropertyChangeObserver` holding `int Position`, a back-reference to the subscription, and implementing `OnChange(in SubjectPropertyChange)` → `subscription.ProcessSegmentCallback(this, in change)`.
  - fields: `readonly Lock _lock`, `readonly IDisposable?[] _segmentHandles`, `readonly PathSegmentObserver?[] _segmentObservers` (the slot-identity record: `_segmentObservers[i]` is the current observer for position `i`), `readonly IInterceptorSubject?[] _resolvedSubjects`, `SubjectPathValue<TValue> _lastObserved`.
  - `void BuildFrom(int startPosition, IInterceptorSubject startSubject)` - subscribe-before-read per segment under `_lock`: for each position `p` from `startPosition`, create a fresh `PathSegmentObserver { Position = p }`, record it in `_segmentObservers[p]`, install `PropertyChangeSubscription.Create(new PropertyReference(subject, segment.PropertyName), observer)` into `_segmentHandles[p]`, THEN read the property value (indexing where applicable) to resolve the next subject; stop when unresolved (suffix stays torn down / null). Store resolved subjects into `_resolvedSubjects`.
  - `void DisposeSuffix(int fromPosition)` - dispose `_segmentHandles[p]` and null `_segmentHandles[p]`, `_segmentObservers[p]`, `_resolvedSubjects[p+1..]` for `p >= fromPosition`.
  - Seed: after the initial `BuildFrom(0, root)`, compute `_lastObserved = PathWalker.Walk<TValue>(...)` so the first delivered event's `Old` reflects the subscribe-time state (no false unresolved→resolved edge on first delivery).

- [ ] **Step 1: Write failing test** - leaf write delivers `ValueChange` with chained `Old`/`New` and the leaf write as `Cause` (this requires `ProcessSegmentCallback` from Task 10, so mark this test `Skip` until Task 10, or write the build-only white-box assertion here). Concretely, write here a white-box test that after subscribe, the number of installed segment handles equals the resolved chain length and the process-wide count equals chain depth:

```csharp
[Fact]
public void WhenSubscribedToResolvedPath_ThenOneListenerPerSegmentIsInstalled()
{
    // Arrange
    PropertyChangeSubscriptions.ResetForTests();
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Person(context) { Father = new Person { FirstName = "Joe" } };

    // Act
    using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { });

    // Assert: two segments (Father, FirstName) => two per-property listeners.
    Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
}
```

- [ ] **Step 2: Run to verify it fails** - Expected: count is 0 (no install yet).

- [ ] **Step 3: Implement** the observer, `BuildFrom`, `DisposeSuffix`, and the `_lastObserved` seed. Wire the constructor to call `BuildFrom(0, root)` under `_lock` and seed. Update `Dispose` to call `DisposeSuffix(0)` under `_lock` (one-shot), clearing references.

- [ ] **Step 4: Run to verify it passes**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): subscribe-before-read chain build with per-segment observers"
```

### Task 10: Event computation - validating walk, retrack on divergence, kind, suppression, `_lastObserved` advance

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathTransitionTests.cs`

**Interfaces:**
- Produces (internal):
  - `void ProcessSegmentCallback(PathSegmentObserver observer, in SubjectPropertyChange cause)` - acquires `_lock`; slot-identity guard: if `_segmentObservers[observer.Position] != observer`, return (stale trailing invocation ignored). If disposed, return. Reentrancy guard is added in Task 12. Then compute the event:
    1. Fresh validating walk from the root into a scratch `IInterceptorSubject?[]` producing `newValue = PathWalker.Walk<TValue>(...)`.
    2. Divergence check: compare the walk's `resolvedSubjects` against `_resolvedSubjects` position by position; the first position where they differ (and both were expected subscribed) is the divergence point. If diverged, `DisposeSuffix(divergencePoint)` then `BuildFrom(divergencePoint, walkSubjectAtDivergence)` (subscribe-before-read), and recompute `newValue` from the retrack's reads (the retrack's reads supersede the initial walk's `New`). In the intact-chain case the walk's `newValue` is used directly.
    3. `Kind`: `ValueChange` when the chain was intact (no divergence) AND `cause.Property` equals the current resolved leaf's `PropertyReference` (leaf subject == `_resolvedSubjects[lastIndex]` and name == leaf segment name) AND `newValue.IsResolved`; else `PathChange`.
    4. Suppression: if `SubjectPathValue<TValue>.AreEquivalent(_lastObserved, newValue)` → suppressed (append nothing, run no own callback), but still drain any backlog (Task 11). Return after draining.
    5. Advance `_lastObserved = newValue` BEFORE the callback runs (so chained transitions survive a throwing callback), build `SubjectPathChange<TValue>(kind, oldObserved, newValue, in cause)` where `oldObserved` is the pre-advance `_lastObserved`, and enqueue/deliver (Task 11).

- [ ] **Step 1: Write failing tests** (`PathTransitionTests.cs`): leaf `ValueChange` with chained Old/New and Cause; heal (assign null intermediate → `PathChange` unresolved→resolved with fresh leaf, intermediate write as Cause); break (null intermediate → `PathChange` resolved→unresolved with last observed as Old); collection replacement moving a different subject to the index → `PathChange` resolved→resolved with both values; collection replacement leaving the same subject and equal leaf value → suppressed (no callback); dictionary key add/replace/remove; present dictionary key with null value → unresolved; single-segment path degenerates; retrack rebuilds only the suffix (white-box: upper handle unchanged).

Representative:

```csharp
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathTransitionTests
{
    public PathTransitionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenLeafWritten_ThenValueChangeDeliveredWithChainedValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c));

        // Act
        father.FirstName = "Jack";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.Old.GetValueOrDefault());
        Assert.Equal("Jack", change.New.GetValueOrDefault());
        Assert.Equal(nameof(Person.FirstName), change.Cause.Property.Name);
    }

    [Fact]
    public void WhenNullIntermediateAssigned_ThenHealDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father null
        SubjectPathChange<string?>? last = null;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => last = c);

        // Act
        person.Father = new Person { FirstName = "Joe" };

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.False(last.Value.Old.IsResolved);
        Assert.True(last.Value.New.IsResolved);
        Assert.Equal("Joe", last.Value.New.GetValueOrDefault());
        Assert.Equal(nameof(Person.Father), last.Value.Cause.Property.Name);
    }
}
```

- [ ] **Step 2: Run to verify they fail**.

- [ ] **Step 3: Implement** `ProcessSegmentCallback` computation (retrack, kind, suppression, advance). Deliver synchronously for now (direct invoke under a released lock); the full drain queue arrives in Task 11.

- [ ] **Step 4: Run to verify they pass**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): event computation with divergent retrack, kind and suppression"
```

### Task 11: Exclusive-drain queue + direct-dispatch fast path

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathConcurrencyTests.cs` (re-entrancy + ordering); `PathTransitionTests` (drain-recovery-after-throw-then-suppress)

**Interfaces:**
- Produces (internal): `readonly Queue<SubjectPathChange<TValue>> _pending`, `bool _draining`, all guarded by `_lock`. Computation appends under the lock; the appending thread becomes drainer if none active and invokes callbacks one at a time with the lock released, re-acquiring per dequeue. Uncontended fast path: when `_pending` is empty and `!_draining`, mark drainer, release lock, invoke the stack-local event directly (skip enqueue/dequeue copies of the large struct), re-acquire to drain anything that raced in and clear the flag. A suppressed event appends nothing and runs no own callback but still claims the drainer and drains any backlog when none is active. A throwing callback propagates to the drainer and abandons the drain; already-queued events stay queued; the queue and `_draining` flag stay consistent. Nested writes from a callback find `_draining` true and enqueue (flattening; total order preserved).

- [ ] **Step 1: Write failing tests**: re-entrancy (a callback that writes another watched segment enqueues a nested event delivered after the current callback returns, flattened, totally ordered); a throwing callback abandons the drain and stranded queued events deliver on the next write with intact chained transitions; drain-recovery-after-throw-then-suppress (throw, then a no-op suppressed write, assert the backlog delivers).

- [ ] **Step 2: Run to verify they fail**.

- [ ] **Step 3: Implement** the drain queue and direct-dispatch fast path.

- [ ] **Step 4: Run to verify they pass**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): exclusive-drain queue with direct-dispatch fast path"
```

### Task 12: Thread-affine reentrancy guard (side-effecting getter during walk)

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathConcurrencyTests.cs`

**Interfaces:**
- Produces (internal): at `ProcessSegmentCallback` entry, before acquiring `_lock` for computation, check `_lock.IsHeldByCurrentThread`; if true (a getter invoked during this tracker's own walk wrote a watched segment and re-entered), set `bool _deferredRevalidation = true` and return without computing. The outer walk, after finishing its computation and BEFORE dispatching, checks `_deferredRevalidation`; if set, clears it and performs a fresh walk / event computation (looping until no deferral remains) so callbacks never run under `_lock`. This closes the AB-BA deadlock a side-effecting getter would otherwise cause.

- [ ] **Step 1: Write failing test**: a subject whose getter writes a watched segment mid-walk (use a `[Derived]`-with-side-effect model or a custom read interceptor) defers a revalidation instead of computing under the lock; assert no deadlock, ordering stays total, and the deferred event is eventually delivered. Use `ManualResetEventSlim`/timeouts, never `Task.Delay`.

- [ ] **Step 2: Run to verify it fails/deadlocks** (bounded by a `WaitAsync` timeout in the test).

- [ ] **Step 3: Implement** the thread-affine guard and deferred-revalidation loop.

- [ ] **Step 4: Run to verify it passes**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): thread-affine reentrancy guard for side-effecting getters"
```

### Task 13: Dispose contract and teardown-on-throw during build

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs`, `SubjectPathSubscriptionExtensions.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathDisposeTests.cs`

**Interfaces:**
- Produces:
  - `Dispose` one-shot: tears down all segment subscriptions (`DisposeSuffix(0)`), clears references, drops queued undelivered events; returns the process-wide count to zero; double dispose is a no-op; `Current` returns unresolved default after dispose; self-dispose from inside a callback is supported (the drainer detects disposal and stops delivering queued events); concurrent `Dispose` racing a direct-dispatch callback coordinates under `_lock` so no new delivery starts once disposal is observed, but a callback already dispatched outside the lock may run to completion (no `ObjectDisposedException`, no torn state - slot-identity guard protects a rebuilt chain).
  - Teardown on throw: if `BuildFrom` (called during the initial subscribe build) throws - only possible via a reserved-key contract violation (`PropertyChangeSubscription.Create` throwing `InvalidOperationException` for a foreign `ni.pcl` value), since name resolution during the build is non-throwing - the subscribe path disposes the segment subscriptions already installed and rolls back the process-wide count before the throw propagates. Implement by wrapping the constructor's build in try/catch that calls `DisposeSuffix(0)` and rethrows; the extension method lets the exception propagate (no partial subscription escapes).

- [ ] **Step 1: Write failing tests**: dispose stops delivery, tears down all segment `Data` entries, returns count to zero, double dispose is a no-op; queued undelivered events dropped on dispose; `Current` returns unresolved default after dispose; self-dispose from inside a callback stops further deliveries without deadlock/exception; teardown-on-throw at a deep segment (occupy a mid-path property's `ni.pcl` key with a foreign value to force `PropertyChangeSubscription.Create` to throw during the build) disposes already-installed segments and restores the count.

```csharp
[Fact]
public void WhenBuildThrowsAtDeepSegment_ThenInstalledSegmentsDisposedAndCountRestored()
{
    // Arrange
    PropertyChangeSubscriptions.ResetForTests();
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var father = new Person { FirstName = "Joe" };
    var person = new Person(context) { Father = father };
    // Occupy the leaf property's reserved listeners key with a foreign value so the second
    // segment install fails loud during BuildFrom.
    new PropertyReference(father, nameof(Person.FirstName))
        .SetPropertyData(PropertyChangeSubscription.ListenersKey, "foreign");

    // Act & Assert
    Assert.Throws<InvalidOperationException>(() =>
        person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }));
    Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
}
```

- [ ] **Step 2: Run to verify they fail**.

- [ ] **Step 3: Implement** the dispose coordination and build teardown.

- [ ] **Step 4: Run to verify they pass**.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking
git commit -m "feat(paths): dispose contract and teardown-on-throw during build"
```

### Task 14: Missed-write window, initial-build race, cyclic path, cross-path deadlock-freedom

**Files:**
- Modify/Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathConcurrencyTests.cs`
- Reuse: `src/Namotion.Interceptor.Tracking.Tests/Change/BlockingWriteInterceptor.cs` (existing) to orchestrate a write parked post-gate, pre-commit.

**Interfaces:** no production change expected (correctness rests on subscribe-before-read + the primitive's post-commit resolution, already merged). If a test exposes a gap, fix in `SubjectPathSubscription.cs`.

- [ ] **Step 1: Write failing/verifying tests**:
  - Missed-write window on retrack: a structural write racing a suffix segment's subscribe-then-read is never lost, both interleavings (writer lookup after install; writer passing the pre-commit gate before install then committing after the tracker's read). Orchestrate with `BlockingWriteInterceptor`.
  - Initial subscribe-time build racing a concurrent write: the racing write is delivered or observed by the initial read.
  - Cyclic path (`x => x.Child.Child.Name` on a self-referencing graph): one write dispatches to the install-order snapshot; the upper observer fires first and its retrack disposes the lower subscription, whose in-snapshot dispatch is skipped by the primitive's cleared-observer guard; assert exactly-once delivery and the multiset chain invariant afterward.
  - Cross-path callbacks: subscription A's callback writes a segment of path B and vice versa, concurrently; no deadlock (pins callbacks running outside `_lock`). Bound with timeouts.
  - Concurrent leaf + structural writes stress test converges to the true graph state (quiescent consistency).
- [ ] **Step 2: Run** - iterate until green; investigate any failure with superpowers:systematic-debugging before touching production code.
- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Paths/PathConcurrencyTests.cs
git commit -m "test(paths): missed-write window, cyclic path, cross-path deadlock-freedom"
```

---

## Phase 5: Transactions

### Task 15: Transaction subscribe-guard, event-walk ambient suppression, stranding regression

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Paths/SubjectPathSubscription.cs` (ambient suppression in the event walk), `SubjectPathSubscriptionExtensions.cs` (subscribe guard, already stubbed in Task 8)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathTransactionTests.cs`

**Interfaces:**
- Consumes: `SubjectTransaction.Current`, `SubjectTransaction.IsCommitting` (internal), `SubjectTransaction.SetCurrent(SubjectTransaction?)` (internal). Tracking is the declaring assembly, so these internals are reachable.
- Produces:
  - Subscribe guard (finalized here): `SubscribeToPath` throws `InvalidOperationException` when `SubjectTransaction.Current is { IsCommitting: false }` (never install a chain on speculative staged subjects a rollback would strand).
  - Event-walk ambient suppression: at the top of `ProcessSegmentCallback`'s computation, when `SubjectTransaction.Current` is non-null, save it, `SubjectTransaction.SetCurrent(null)` for the duration of the validating walk and any retrack reads, and restore it in a `finally`. This keeps every retrack decision on committed state (a real `[Derived]`-with-setter or cross-context write on a transaction-holding flow would otherwise read staged values and persistently retrack onto a speculative subject). Cost is one AsyncLocal write, paid only when a transaction is active during dispatch. `Current` (the public read) is deliberately left unconstrained (returns the transaction's read-your-writes view).

- [ ] **Step 1: Write failing tests**:
  - Subscribe inside an active non-committing transaction throws `InvalidOperationException`.
  - A subscription created outside a transaction survives a transaction that stages then rolls back a watched segment, with no phantom chain (the rollback stages nothing real, chain stays on the committed subject).
  - Commit replay delivers; a transaction disposed without commit delivers nothing; a failed commit that reverts delivers the apply-and-revert pair and converges to the pre-transaction observed state.
  - Transaction retrack-stranding regression (the exact sequence from the spec): subscription on `root.Child(A).Value`; open a transaction and stage `root.Child = B`; make a real `[Derived]`-with-setter write on A (bypasses staging, dispatches immediately on the transaction flow, triggering an event walk); assert the walk reads committed A, installs no B listener, and does NOT dispose A's `Value` listener (white-box `ni.pcl` check); then dispose the transaction without commit; assert the chain is still on A, a subsequent write to `A.Value` delivers, and no B listener leaked.
  - Callback exceptions during commit replay are caught by the transaction apply loop (`SubjectPropertyChangeOperations.TryApplyLocalChange`) and surfaced as `SubjectTransactionException`, not propagated to the writer (the documented composition edge); outside transactions the same throwing callback propagates to the drainer.

  These require a model with a settable `[Derived]` property and a `Child`/`Value` shape; add to `PathModels.cs` if not present.

- [ ] **Step 2: Run to verify they fail**.

- [ ] **Step 3: Implement** the ambient suppression in the event walk and finalize the subscribe guard.

- [ ] **Step 4: Run to verify they pass** (run `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PathTransactionTests"`).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests/Paths/PathTransactionTests.cs
git commit -m "feat(paths): transaction subscribe-guard and committed-state event walk"
```

---

## Phase 6: Dormancy and carve-outs

### Task 16: Dormancy, context-inheritance prerequisite, dormant-divergence heal, derived-intermediate carve-out, Current-is-side-effect-free

**Files:**
- Modify/Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathDormancyTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathModels.cs` (derived subject-typed intermediate whose child is held only via the derived property; a derived intermediate that aliases an intercepted-held child)

**Interfaces:** no production change expected (dormancy is inherited from the primitive; the revalidating walk already heals). If a test exposes a gap, fix in the tracker.

- [ ] **Step 1: Write verifying tests**:
  - Exclusively-owned subtree: no delivery while the root is detached; delivery resumes on re-attach; a structural change made while dormant is missed at the time it happens and the chain re-syncs on the next delivered callback; `Current` stays fresh throughout.
  - Dormant-divergence heal via the stale branch: replace an intermediate while dormant, move the old suffix subject into another attached notifying context, then write the old subject's leaf; the subscription must NOT deliver the off-path value - the revalidating walk detects divergence, retracks onto the true path, and delivers a `PathChange` reflecting the true observed state (or suppresses if unchanged); afterwards writes to the new suffix deliver and writes to the old subject do not.
  - Divergent-retrack freshness: orchestrate a write to the new suffix that commits between the validating walk's read and the retrack's install; the delivered `New` (or an immediately following event) reflects that write (pins that `New` comes from the retrack's subscribe-before-read reads, not the pre-retrack walk). Use `BlockingWriteInterceptor`.
  - Bare `WithPropertyChangeSubscriptions()` (no context inheritance): a multi-segment path whose child is assigned context-free is inert below the root; the same path with `WithFullPropertyTracking()`, or with a child carrying its own notifying context, delivers.
  - Newly-assigned child (dispatch-after-lifecycle prerequisite): a callback reacting to the structural change writes a property of the just-assigned, previously context-free child; the write is intercepted and delivered as its own event.
  - Derived-intermediate exclusive-hold: a derived subject-typed intermediate whose child is referenced by no intercepted property - subscribe, write the child's leaf before any recalculation, assert no delivery; change a derived input to force an `Equals`-distinct recalculation, assert the suffix attaches, heals, and subsequently delivers; assert an `Equals`-equal recompute (distinct-but-equal instance) does NOT attach or heal (pins the equality-suppression carve-out).
  - Trailing invocation from a disposed suffix subscription is ignored; a legitimate callback from a surviving upper segment after a suffix rebuild is NOT ignored (pins slot-identity against the generation-counter mistake).
  - `Current` is side-effect free: reading `Current` while the chain is diverged (stale after a dormant divergence) does not retrack or heal it (white-box: the subscribed chain is unchanged after the read).
  - Traversal-failure carve-out (getter or container op): a getter, or a container `Count`/indexer/`TryGetValue`/comparer, that throws on one walk publishes unresolved; merely clearing the failing condition does NOT heal the event stream while `Current` already reflects truth; only a subsequent watched-segment callback re-walks and heals. Run for both a transient getter throw and a transient container throw.
  - A getter that throws during a walk makes the path unresolved from that segment instead of propagating into the writer's chain; a leaf whose runtime value is not assignable to `TValue` resolves as unresolved (no `InvalidCastException`); a hostile custom container whose members throw resolves the segment as unresolved.

- [ ] **Step 2: Run** - iterate until green; debug any surprise with superpowers:systematic-debugging.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Paths/PathDormancyTests.cs src/Namotion.Interceptor.Tracking.Tests/Paths/PathModels.cs
git commit -m "test(paths): dormancy, context-inheritance prerequisite and carve-outs"
```

---

## Phase 7: Property-type matrix, chain invariant, cause, fire-and-forget, snapshot

### Task 17: Property-type matrix

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathModels.cs` (interface intermediate, `[Derived]` interface-default leaf, `int`-keyed dictionary, `List<T>`/`IList<T>` subjects, `int?` leaf, non-`IEquatable` struct leaf, case-insensitive generic-only dictionary)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathPropertyTypeMatrixTests.cs`

**Interfaces:** no production change expected. Each test covers BOTH the walk and a retrack.

- [ ] **Step 1: Write tests**, one per shape (spec "Property type matrix"): scalar leaves (`int`, `double`, `string`, `int?`, `string?` where null is a resolved value distinct from unresolved); subject-reference leaf (`TValue` is a subject type; suppression is reference equality); subject reference intermediate incl. nullable; subject collection intermediates `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ImmutableArray<T>`; subject dictionary intermediates `Dictionary<string,T>`, `IDictionary<string,T>`, `IReadOnlyDictionary<string,T>`, and one non-string key type; interface-typed intermediate; `[Derived]` interface-default property as leaf (plain interface default rejected - assert throw); derived leaf and derived subject-typed intermediate (with derived-change detection; inert without); a deep mixed path combining reference, collection and dictionary segments; custom-comparer dictionary (case-insensitive, including a generic-only implementation) resolves a comparer-matched key.

Use a shared helper to reduce duplication (subscribe, mutate, assert transition), but keep each `[Fact]` named `When<Shape>_Then<Behavior>`.

- [ ] **Step 2: Run** to green.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Paths
git commit -m "test(paths): property-type matrix across leaf, collection and dictionary shapes"
```

### Task 18: Chain invariant (no stale subscriptions), cause/provenance, fire-and-forget GC

**Files:**
- Test: `src/Namotion.Interceptor.Tracking.Tests/Paths/PathChainInvariantTests.cs`, `PathCauseTests.cs`

**Interfaces:** no production change expected. White-box assertions read the `ni.pcl` listener arrays from `subject.Data[(name, PropertyChangeSubscription.ListenersKey)]` (internal key reachable via InternalsVisibleTo) and the process-wide `PropertyChangeSubscriptions.ReadSubscriptionCount()`.

- [ ] **Step 1: Write tests**:
  - Chain invariant: after every observed structural change the multiset of installed segment listeners equals exactly the current resolved chain's segments (a cyclic path holds two listeners for a doubly-appearing subject). Replacing an intermediate disposes the old suffix's listener entries (white-box `ni.pcl` on the replaced subject and below is empty; count equals chain length). A write to a subject moved off the path delivers nothing. A write to the newly resolved leaf delivers for heal, re-resolve via collection replacement, and re-resolve via dictionary-key replacement. Breaking (null intermediate, out-of-range index, key removed) disposes the entire suffix below the break; only the resolvable prefix keeps listeners. Repeated churn (replace/break/heal loop) never grows the count beyond the current chain length (leak check).
  - Cause: `Kind`, triggering property, and origin passthrough (`Cause.Origin` equals the triggering write's origin verbatim). Origin-is-not-provenance: orchestrate a `FromSource` write whose dispatch is delayed past a `Local` write's commit; the single delivered event carries the `Local` value as `New` under `Cause.Origin` `FromSource`, and the `Local` write's own callback is suppressed (document that per-write origin fidelity needs the primitive or queue channel). Use `BlockingWriteInterceptor` and `ChangeOrigin.FromSource(...)` via `PendingOrigin`/source write helpers used elsewhere in the tests.
  - Fire-and-forget: dropped root plus dropped handle is collectable (`WeakReference` GC test; first pin of the collected-together behavior).

- [ ] **Step 2: Run** to green.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Paths
git commit -m "test(paths): chain invariant, cause provenance and fire-and-forget collection"
```

### Task 19: Public API snapshot accept

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Run the snapshot test** (expects failure with a `.received.txt` diff adding the `Namotion.Interceptor.Tracking.Paths` public types):

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"`
Expected: FAIL with a diff introducing `SubjectPathValue<TValue>`, `SubjectPathChange<TValue>`, `SubjectPathChangeKind`, `SubjectPathChangeCallback<TValue>`, `ISubjectPathChangeObserver<TValue>`, `SubjectPathSubscription<TValue>`, and `SubjectPathSubscriptionExtensions.SubscribeToPath` overloads; the core snapshot (`Namotion.Interceptor`) is unchanged.

- [ ] **Step 2: Review the diff** to confirm ONLY the intended public surface appears (no accidental public members; the internal tracker helpers stay internal).

- [ ] **Step 3: Accept the snapshot** by replacing `.verified.txt` with the `.received.txt` output.

- [ ] **Step 4: Re-run** to verify PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "test(paths): accept public API snapshot for path subscriptions"
```

---

## Phase 8: Benchmarks (benchmark-gated; requires-benchmark label)

### Task 20: `SubjectPathSubscriptionBenchmark`

**Files:**
- Create: `src/Namotion.Interceptor.Benchmark/SubjectPathSubscriptionBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/Program.cs` (register the benchmark)
- May add benchmark models to `src/Namotion.Interceptor.Benchmark/` (a deep path subject with reference/collection/dictionary/ImmutableArray segments).

**Interfaces:** `[MemoryDiagnoser]` class following `PropertyChangeSubscriptionsBenchmark` conventions (per-state `[GlobalSetup(Target = ...)]`, one process per state since the subscription count is process-wide static, context registered with `WithFullPropertyTracking`).

- [ ] **Step 1: Implement benchmarks** covering the spec's benchmark set:
  - Leaf-write delivery through a path subscription versus a bare per-property listener on the same property, at depths 1 and 4 (exposes the walk's O(depth) scaling); include the hot-leaf worst case (repeated writes to one watched leaf where every processed callback pays the walk even when suppressed).
  - Allocation gate: delivery is allocation-free on the path side for inline-sized value types and strings, INCLUDING a path with an `ImmutableArray<T>` intermediate segment (the typed accessor covers value-typed intermediates, not only the leaf). Scope the gate to `IEquatable<T>` value types and strings (a non-`IEquatable<T>` struct leaf boxes both operands in the `EqualityComparer<TValue>.Default` suppression comparison - measure but do not gate).
  - A structural write triggering a suffix retrack at a four-segment path, measuring the inline writer cost (suffix disposal + subscribe-and-read walk) and its allocations.
  - `Current` read at depths 1 and 4 (O(depth) lock-free walk, no caching), guarding the read-only consumer cost independently of delivery.
  - Subscribe-and-dispose churn at a representative depth (O(depth) install/teardown plus the first-of-type leaf-accessor compile).
  - A deep mixed path (reference + collection + dictionary in one expression) guarding per-segment resolution across shapes.

- [ ] **Step 2: Build the benchmark project** in Release: `dotnet build src/Namotion.Interceptor.Benchmark -c Release` - Expected: succeeds.

- [ ] **Step 3: Smoke-run one benchmark** locally (short job) to confirm it executes; full runs happen in CI under the `requires-benchmark` label. Assert (via the `[MemoryDiagnoser]` Allocated column) that the gated cases are 0 B/op on the path side.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Benchmark
git commit -m "bench(paths): path subscription delivery, retrack, Current and churn benchmarks"
```

---

## Phase 9: Documentation

### Task 21: `docs/tracking.md` path-subscriptions section

**Files:**
- Modify: `docs/tracking.md` (new `### Path Subscriptions` after `### Per-Property Subscriptions`, before `### Concurrency and Delivery` or as a peer section; match the existing heading style)

- [ ] **Step 1: Write the section** covering (spec "Docs"):
  - The two `SubscribeToPath` overloads with a worked example seeded from `Current`; note `SubscribeToProperty` is the lighter choice for a single-segment path. Include the subscribe-then-read-`Current` non-atomicity seam and the safe caching-consumer seeding pattern (subscribe first, then apply the seed and every callback under one lock reading `Current` at apply time, never a cached snapshot or the event `New`).
  - Observed-state model (`SubjectPathValue<TValue>` resolved/unresolved; resolved-null distinct from unresolved) and transition suppression (`Old == New` under `EqualityComparer<TValue>.Default`).
  - Delivery contract including the FIVE quiescent-consistency carve-outs: (1) throwing callbacks; (2) a dormant divergence with no later delivered callback; (3) a foreign synchronous observer throwing on a shared write, aborting dispatch before the segment callback; (4) a getter or container operation throwing during the walk, recovering only on a later delivered segment callback; (5) an `Equals`-suppressed recompute of a derived subject-valued intermediate leaving the chain on the old subtree until the next non-suppressed segment callback.
  - Dormancy and the context-inheritance prerequisite: use `WithFullPropertyTracking()` (or at least `WithPropertyChangeSubscriptions()` + `WithContextInheritance()`) for paths spanning more than one subject; the derived-intermediate exclusive-hold caveat.
  - Ownership/lifetime (mandatory dispose; the forgotten-handler cost multiplied across the chain), transaction visibility (subscribe disallowed inside a non-committing transaction; commit-replay delivery; `Current` returns the transaction's read-your-writes view).
  - The cost model, using the spec's wording verbatim (the "While at least one per-property or path subscription exists anywhere in the process..." paragraph, including the `[Derived]`-segment and derived-subject-valued-intermediate warnings).
  - Thread-marshaling note for UI consumers (callbacks run on writer/draining threads, never a UI thread; Blazor must marshal via `InvokeAsync`).
  - A final info block titled "Future extensions" mirroring the spec's out-of-scope list (prefix sharing; string-path subscriptions; dynamic/runtime-named segments and late-add discovery, issue #387; cast support in selectors; paths ending in an indexed element; in-place collection mutation tracking; asynchronous delivery; `Refresh()`).
  - No em dashes; restructure into plain sentences.

- [ ] **Step 2: Verify the doc renders** and links resolve (visual check; no test).

- [ ] **Step 3: Commit**

```bash
git add docs/tracking.md
git commit -m "docs(paths): document path subscriptions, delivery contract and cost model"
```

### Task 22: Full suite green + self-review pass

**Files:** none (verification).

- [ ] **Step 1: Full unit-test run**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests`
Expected: all pass, including `PerPropertySubscriptionConventionsTests` (confirms every `SubscribeToPath` test file joined the serialized collection).

- [ ] **Step 2: Solution build with warnings-as-errors**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: succeeds with no warnings.

- [ ] **Step 3: Broader test sweep**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: pass (no regressions in dependent Tracking consumers).

- [ ] **Step 4: Commit any final fixes**, then hand off for review (do not merge; PR needs the `requires-benchmark` label for the benchmark task).

---

## Self-Review

**1. Spec coverage** (each spec section → task):
- Public surface → Tasks 1, 2, 8, 19.
- Expression rules / validation boundary (three tiers) → Tasks 4, 5 (static tier); Task 7 + Task 16 (runtime tier downgrades to unresolved); collection type coverage → Task 17.
- No initial callback; `Current` seeds initial state → Task 8; safe caching-consumer pattern is a docs item → Task 21.
- Mechanism (segment chain, cached getter, typed accessors, subscribe-before-read, dispatch-after-lifecycle, stale-callback slot-identity guard, index bounds/dictionary typed lookup, walk-failure resolve-to-unresolved) → Tasks 6, 7, 9, 10; dispatch-after-lifecycle verified in Task 16.
- Observed-state model and transition suppression (New/Old/suppression, WithEqualityCheck note, chained transitions, coalescing) → Task 10; derived-intermediate carve-out → Task 16.
- Delivery contract (drain queue, direct-dispatch, reentrancy guard, five carve-outs, callback must-not-throw, writer cost model, re-entrancy, getters under lock, cross-subscription independence, `Current` purity) → Tasks 10, 11, 12, 16.
- Cause and provenance → Task 18.
- Ownership and lifetime (dispose one-shot, fire-and-forget, count increment/decrement, teardown-on-throw) → Tasks 13, 18.
- Dormancy (inherited; context-inheritance prerequisite; derived-intermediate carve-out; dispatch-after-lifecycle non-dormancy) → Task 16.
- Transactions (subscribe guard, committed-state event walk, stranding regression, apply-and-revert, commit-replay throwing callback composition edge) → Task 15.
- Package placement and files → File Structure + all tasks; Registry-free constraint is in Global Constraints and verified by the build.
- Testing/verification clusters → Tasks 4/5, 8, 10, 13, 14, 15, 16, 17, 18.
- Performance (benchmark-gated) → Task 20.
- Docs → Task 21.
- Out-of-scope (dynamic/late-add #387, sequence numbers #385) → explicitly excluded; #387 items appear only in the docs "Future extensions" block (Task 21). No implementation task references them.

**2. Placeholder scan:** production types carry full code; tests give representative full bodies plus concretely-specified enumerations (each names a model, an act, and an assert). No "TBD"/"handle edge cases"/"write tests for the above".

**3. Type consistency:** `SubjectPathValue<TValue>` (`IsResolved`, `Value`, `TryGetValue`, `GetValueOrDefault`, `Resolved`/`Unresolved`, internal `AreEquivalent`), `SubjectPathChange<TValue>` (`Kind`, `Old`, `New`, `Cause`, internal ctor), `SubjectPathChangeKind` (`ValueChange`/`PathChange`), `SubjectPathChangeCallback<TValue>`, `ISubjectPathChangeObserver<TValue>.OnChange`, `SubjectPathSubscription<TValue>` (`Current`, `Dispose`), `SubscribeToPath` overloads, `PathSegment`/`PathSegmentKind`, `PathExpressionDecomposer.Decompose`, `PathValueAccessors.{GetLeafAccessor,GetImmutableArrayIndexer,GetDictionaryLookup,IndexReferenceCollection}`, `PathWalker.Walk`, tracker internals (`BuildFrom`, `DisposeSuffix`, `ProcessSegmentCallback`, `PathSegmentObserver`, `_segmentObservers`, `_lastObserved`, `_pending`, `_draining`, `_deferredRevalidation`) are used consistently across tasks.

---

## Blocking assumptions / spec gaps

One genuine spec gap was found; the remainder are non-blocking clarifications recorded for the implementer.

**GAP 1 (real, internal only - value-typed intermediate accessor signature).** Spec "Mechanism", the typed-accessor requirement (design doc lines ~134): it says the leaf and any value-typed intermediate collection segment are read through "a typed compiled accessor (a `Func<IInterceptorSubject, TSegment>` built from the runtime-resolved `PropertyInfo`)". That literal signature works for the **leaf** (there `TSegment == TValue`, which the generic tracker knows statically, so a `Func<IInterceptorSubject, TValue>` returns the value with no box). It does **not** work for a **value-typed intermediate collection** such as `ImmutableArray<T>`: the tracker is generic only over the leaf `TValue`, so it cannot hold a statically-typed local of the intermediate's collection type, and invoking a `Func<IInterceptorSubject, ImmutableArray<T>>` through a non-generic `Delegate`/`object` boxes the returned struct - which fails the benchmark gate that explicitly requires allocation-free delivery through an `ImmutableArray<T>` intermediate. This plan resolves it by making the value-typed intermediate accessor a **read-and-index compiled delegate** that reads the typed `ImmutableArray<T>`, bounds-checks, and returns the element as a reference-typed `IInterceptorSubject?` (`Func<IInterceptorSubject, int, IInterceptorSubject?>`, Task 6). This is internal only (no public API impact) and consistent with the spec's stated intent ("keeps the whole walk allocation-free, not only the leaf"); it just picks a delegate shape the literal wording does not. A wrong guess here (following the literal `Func<…, TSegment>` and boxing) would pass functional tests but fail the Task 20 allocation gate, forcing a redesign of the accessor layer - hence flagged.

**Non-blocking clarifications** (spec is sufficient; recording the reading used so the implementer does not re-derive them):

- N1. Segment observer / slot identity: the "current subscription object recorded for that position" is realized as the tracker's own `PathSegmentObserver` instance per position (a fresh one per (re)subscribe), compared by reference under `_lock`. The primitive returns `IDisposable` from `Create` and dispatches to the `IPropertyChangeObserver` we pass, so the observer is the natural slot-identity token.
- N2. Ambient-transaction suppression uses `SubjectTransaction.SetCurrent(null)`/restore around the event walk; `SetCurrent`, `Current`, and `IsCommitting` are `internal` on `SubjectTransaction` and reachable because the feature lives in the same assembly. Confirmed against `SubjectTransaction.cs` / `SubjectTransactionInterceptor.cs`.
- N3. The typed leaf/intermediate accessors invoke the property's public getter (`Expression.Property(Expression.Convert(param, declaringType), propertyInfo)`), which the generated partial property routes through the read-interceptor chain (`GetPropertyValue` → `_context.GetPropertyValue`). So a compiled accessor still honors `SyncRoot`, the derived read hook, and (for `Current`) a transaction's staged view, while avoiding the box that `metadata.GetValue`'s `object` return imposes. Confirmed against `SubjectCodeGenerator.cs`.
- N4. `SubscribeToPath` requires no new registration extension. It composes the existing `PropertyChangeInterceptor`; delivery for multi-segment paths additionally needs `ContextInheritanceHandler` (via `WithContextInheritance()`, bundled by `WithFullPropertyTracking()`), which is a documented prerequisite, not an install-time throw. Confirmed against `InterceptorSubjectContextExtensions.cs`.
- N5. Test white-box assertions read the listener arrays via the internal `PropertyChangeSubscription.ListenersKey` (`"ni.pcl"`) and `PropertyChangeSubscriptions.ReadSubscriptionCount()`; both are reachable via the existing `InternalsVisibleTo("Namotion.Interceptor.Tracking.Tests")`. Confirmed against the csproj.

No other point in the spec required guessing an API shape, type, data structure, or behavior whose wrong guess would cause rework.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-07-21-path-subscriptions.md`. Two execution options:**

**1. Subagent-Driven (recommended)** - dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** - execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
