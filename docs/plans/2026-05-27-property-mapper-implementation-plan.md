# Property Mapper Abstraction Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Introduce a unified `IPropertyMapper<TMapping>` abstraction in `Namotion.Interceptor.Connectors`, migrate every existing connector except TwinCAT to use it, document the new abstraction, then migrate TwinCAT on a follow-up branch.

**Architecture:** A generic mapper interface (`IPropertyMapper<TMapping>` with `TryGetMapping(property, out mapping)`) plus three generic implementations (delegate, composite, fluent base) live in `Namotion.Interceptor.Connectors`. Each connector defines its own `TMapping` record (`OpcUaPropertyMapping`, `MqttPropertyMapping`, `WebSocketPropertyMapping`, later `AdsPropertyMapping`) and thin wrapper classes (attribute mapper, path-provider adapter, fluent mapper). Configuration objects default `Mapper` to a static composite (path provider + attribute mapper), matching OPC UA's existing pattern at `OpcUaClientConfiguration.cs:11-13`. The full design is in `docs/plans/2026-05-27-property-mapper-abstraction-design.md` — **read that first**.

**Tech Stack:** C# 13 with static abstract interface members, .NET 9.0, xUnit, the existing test conventions from `CLAUDE.md` (Arrange/Act/Assert comments, `When<Condition>_Then<Behavior>` naming, no hardcoded waits).

## Branch strategy

This plan produces two independently mergeable PRs:

```
master
  └── feature/property-mapper-abstraction       ← Phases 1-5 (this plan)
        └── feature/add-twincat-connector       ← Phase 6 (rebased after mapper PR lands)
```

**Mapper branch** (`feature/property-mapper-abstraction`): cuts from master, contains foundation + OPC UA migration + MQTT migration + WebSocket migration + documentation update. Merges to master independently.

**TwinCAT branch** (`feature/add-twincat-connector`): rebases onto the merged mapper branch, then adds the TwinCAT migration (Phase 6). The TwinCAT connector itself is brand new on this branch and not yet on master; rebasing brings the mapper abstraction in scope, after which the TwinCAT migration tasks apply.

## Phase 0: Set up the mapper branch

### Task 0: Cut the mapper branch from master

**Step 1: Confirm working tree is clean**

Run: `git status`

Expected: clean working tree on whatever branch is current.

**Step 2: Fetch and create the new branch off master**

```bash
git fetch origin
git checkout -b feature/property-mapper-abstraction origin/master
```

**Step 3: Verify the branch base**

Run: `git log --oneline -3`

Expected: most recent commit is master's tip (e.g., `1f62ff6f Add OPC UA client diagnostics ...`).

---

## Phase 1: Foundation primitives in `Namotion.Interceptor.Connectors`

**Pre-flight reading:**
- `docs/plans/2026-05-27-property-mapper-abstraction-design.md` (required)
- `src/Namotion.Interceptor.OpcUa/Mapping/IOpcUaNodeMapper.cs` (reference pattern this plan generalizes)
- `src/Namotion.Interceptor.OpcUa/Mapping/CompositeNodeMapper.cs` (reference for composite semantics)
- `src/Namotion.Interceptor.OpcUa/Mapping/FluentOpcUaNodeMapper.cs` (reference for fluent mapper)
- `CLAUDE.md` (priorities and test conventions)

**Resolved open questions:**
- **Absolute-path detection (used by TwinCAT in Phase 6):** loader treats any non-null path field returned by the mapper as the complete path. No `IsAbsolutePath` flag. Path-provider mapper produces fully-resolved paths (via `TryGetPath` walking parents).
- **Fluent mapper keying:** match `FluentOpcUaNodeMapper.cs:62-80` — key by property-name path string from the lambda expression body, lookup-time path computed by walking `RegisteredSubjectProperty.Subject.Parent`.

### Task 1: Add `IPropertyMapping<TSelf>` marker interface

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapping.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/PropertyMappingTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/Mapping/PropertyMappingTests.cs
using Namotion.Interceptor.Connectors.Mapping;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class PropertyMappingTests
{
    private sealed record TestMapping(string? A, int? B) : IPropertyMapping<TestMapping>
    {
        public static TestMapping Merge(TestMapping over, TestMapping fallback) => new(
            A: over.A ?? fallback.A,
            B: over.B ?? fallback.B);
    }

    [Fact]
    public void WhenMergingPartialMappings_ThenOverridePrecedesFallback()
    {
        // Arrange
        var fallback = new TestMapping(A: "fallback", B: 1);
        var over = new TestMapping(A: "over", B: null);

        // Act
        var merged = TestMapping.Merge(over, fallback);

        // Assert
        Assert.Equal("over", merged.A);
        Assert.Equal(1, merged.B);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~PropertyMappingTests"`

Expected: COMPILE FAIL ("type or namespace `IPropertyMapping<>` not found").

**Step 3: Add the interface**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapping.cs
namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Marker interface for property-mapping records produced by an <see cref="IPropertyMapper{TMapping}"/>.
/// Records implementing this contract know how to merge two partial mappings into a combined result.
/// </summary>
public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    /// <summary>
    /// Merge <paramref name="over"/> on top of <paramref name="fallback"/>. Override fields take
    /// precedence; null fields fall through to the fallback.
    /// </summary>
    static abstract TSelf Merge(TSelf over, TSelf fallback);
}
```

**Step 4: Run test to verify it passes**

Expected: PASS.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapping.cs \
        src/Namotion.Interceptor.Connectors.Tests/Mapping/PropertyMappingTests.cs
git commit -m "feat(connectors): add IPropertyMapping<TSelf> marker for partial-mapping merge"
```

### Task 2: Add `IPropertyMapper<TMapping>` interface

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapper.cs`

**Step 1: Add the interface**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapper.cs
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Maps a <see cref="RegisteredSubjectProperty"/> to a connector-specific <typeparamref name="TMapping"/>.
/// Returns <c>false</c> when the property is not mapped (excluded).
/// </summary>
public interface IPropertyMapper<TMapping>
{
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping);
}
```

**Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.Connectors`

Expected: SUCCESS.

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/IPropertyMapper.cs
git commit -m "feat(connectors): add IPropertyMapper<TMapping> interface"
```

### Task 3: Add `IReversePropertyMapper<TMapping, TKey>` interface

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/IReversePropertyMapper.cs`

**Step 1: Add the interface**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/IReversePropertyMapper.cs
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Extends <see cref="IPropertyMapper{TMapping}"/> with reverse lookup: given a connector-specific
/// key (e.g. MQTT topic, OPC UA node reference), resolve the matching property in the subject graph.
/// </summary>
public interface IReversePropertyMapper<TMapping, TKey> : IPropertyMapper<TMapping>
{
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject,
        TKey key,
        CancellationToken cancellationToken);
}
```

**Step 2: Build and commit**

```bash
dotnet build src/Namotion.Interceptor.Connectors
git add src/Namotion.Interceptor.Connectors/Mapping/IReversePropertyMapper.cs
git commit -m "feat(connectors): add IReversePropertyMapper<TMapping, TKey> interface"
```

### Task 4: Add `DelegatePropertyMapper<TMapping>` with TDD

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/DelegatePropertyMapper.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/DelegatePropertyMapperTests.cs`

**Step 1: Write the failing tests**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/Mapping/DelegatePropertyMapperTests.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class DelegatePropertyMapperTests
{
    private sealed record TestMapping(string Value);

    [InterceptorSubject]
    public partial class Sample
    {
        public partial string Name { get; set; }
    }

    [Fact]
    public void WhenSelectorReturnsValue_ThenTryGetMappingReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sample = new Sample(context) { Name = "x" };
        var nameProperty = sample.TryGetRegisteredSubject()!.TryGetProperty(nameof(Sample.Name))!;

        var mapper = new DelegatePropertyMapper<TestMapping>(p => new TestMapping(p.Name));

        // Act
        var found = mapper.TryGetMapping(nameProperty, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Name", mapping!.Value);
    }

    [Fact]
    public void WhenSelectorReturnsNull_ThenTryGetMappingReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sample = new Sample(context);
        var nameProperty = sample.TryGetRegisteredSubject()!.TryGetProperty(nameof(Sample.Name))!;

        var mapper = new DelegatePropertyMapper<TestMapping>(_ => null);

        // Act
        var found = mapper.TryGetMapping(nameProperty, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
```

**Step 2: Run to verify failure**

Expected: COMPILE FAIL.

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/DelegatePropertyMapper.cs
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// A property mapper that delegates to a user-supplied function. Useful for wrapping
/// attribute lookups, path-provider conversions, or any per-property mapping derivation.
/// </summary>
public class DelegatePropertyMapper<TMapping> : IPropertyMapper<TMapping>
{
    private readonly Func<RegisteredSubjectProperty, TMapping?> _selector;

    public DelegatePropertyMapper(Func<RegisteredSubjectProperty, TMapping?> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = _selector(property);
        return mapping is not null;
    }
}
```

**Step 4: Run to verify pass**

Expected: PASS (2 tests).

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/DelegatePropertyMapper.cs \
        src/Namotion.Interceptor.Connectors.Tests/Mapping/DelegatePropertyMapperTests.cs
git commit -m "feat(connectors): add DelegatePropertyMapper<TMapping>"
```

### Task 5: Add `CompositePropertyMapper<TMapping>` with TDD

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/CompositePropertyMapper.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/CompositePropertyMapperTests.cs`

**Step 1: Write the failing tests**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/Mapping/CompositePropertyMapperTests.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class CompositePropertyMapperTests
{
    private sealed record TestMapping(string? A, int? B) : IPropertyMapping<TestMapping>
    {
        public static TestMapping Merge(TestMapping over, TestMapping fallback) =>
            new(over.A ?? fallback.A, over.B ?? fallback.B);
    }

    [InterceptorSubject]
    public partial class Sample { public partial string Name { get; set; } }

    private static RegisteredSubjectProperty NameProperty()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var s = new Sample(context) { Name = "x" };
        return s.TryGetRegisteredSubject()!.TryGetProperty(nameof(Sample.Name))!;
    }

    [Fact]
    public void WhenAllInnerMappersReturnNull_ThenCompositeReturnsFalse()
    {
        // Arrange
        var mapper = new CompositePropertyMapper<TestMapping>(
            new DelegatePropertyMapper<TestMapping>(_ => null),
            new DelegatePropertyMapper<TestMapping>(_ => null));

        // Act
        var found = mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenLaterMapperHasFields_ThenItOverridesEarlierMapperFields()
    {
        // Arrange
        var mapper = new CompositePropertyMapper<TestMapping>(
            new DelegatePropertyMapper<TestMapping>(_ => new TestMapping(A: "first", B: 1)),
            new DelegatePropertyMapper<TestMapping>(_ => new TestMapping(A: "second", B: null)));

        // Act
        mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("second", mapping.A);
        Assert.Equal(1, mapping.B);
    }

    [Fact]
    public void WhenExplicitMergerProvided_ThenItOverridesDefaultMerge()
    {
        // Arrange
        var mapper = new CompositePropertyMapper<TestMapping>(
            (over, fallback) => new TestMapping(A: fallback.A, B: over.B ?? fallback.B),
            new DelegatePropertyMapper<TestMapping>(_ => new TestMapping(A: "first", B: 1)),
            new DelegatePropertyMapper<TestMapping>(_ => new TestMapping(A: "second", B: 2)));

        // Act
        mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("first", mapping.A);
        Assert.Equal(2, mapping.B);
    }
}
```

**Step 2: Run to verify failure**

Expected: COMPILE FAIL.

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/CompositePropertyMapper.cs
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Combines multiple <see cref="IPropertyMapper{TMapping}"/> instances. Inner mappers run
/// left-to-right; non-null results are merged on top of the accumulated mapping as overrides.
/// Final result is <c>null</c> only if every inner mapper returns <c>null</c>.
/// </summary>
public class CompositePropertyMapper<TMapping> : IPropertyMapper<TMapping>
    where TMapping : IPropertyMapping<TMapping>
{
    private readonly Func<TMapping, TMapping, TMapping> _merge;
    private readonly IPropertyMapper<TMapping>[] _mappers;

    /// <summary>Uses <see cref="IPropertyMapping{TSelf}.Merge"/> as the merge function.</summary>
    public CompositePropertyMapper(params IPropertyMapper<TMapping>[] mappers)
        : this(TMapping.Merge, mappers) { }

    /// <summary>Uses the explicit <paramref name="merge"/> function.</summary>
    public CompositePropertyMapper(
        Func<TMapping, TMapping, TMapping> merge,
        params IPropertyMapper<TMapping>[] mappers)
    {
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        _mappers = mappers ?? throw new ArgumentNullException(nameof(mappers));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = default;
        var found = false;
        foreach (var inner in _mappers)
        {
            if (inner.TryGetMapping(property, out var partial))
            {
                mapping = found ? _merge(partial, mapping!) : partial;
                found = true;
            }
        }
        return found;
    }
}
```

**Step 4: Run to verify pass**

Expected: PASS (3 tests).

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/CompositePropertyMapper.cs \
        src/Namotion.Interceptor.Connectors.Tests/Mapping/CompositePropertyMapperTests.cs
git commit -m "feat(connectors): add CompositePropertyMapper<TMapping> with default+explicit merge ctors"
```

### Task 6: Add `FluentPropertyMapperBase<TSubject, TMapping>` with TDD

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/FluentPropertyMapperBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentPropertyMapperBaseTests.cs`

**Step 1: Write the failing tests**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentPropertyMapperBaseTests.cs
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentPropertyMapperBaseTests
{
    private sealed record TestMapping(string? Value);

    [InterceptorSubject]
    public partial class Motor { public partial double Speed { get; set; } }

    [InterceptorSubject]
    public partial class Plant { public partial Motor Motor { get; set; } }

    private sealed class TestFluent : FluentPropertyMapperBase<Plant, TestMapping>
    {
        public TestFluent Set<TValue>(Expression<Func<Plant, TValue>> selector, TestMapping mapping)
        {
            SetMapping(selector, mapping);
            return this;
        }
    }

    [Fact]
    public void WhenPropertyConfigured_ThenTryGetMappingReturnsConfiguredValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var plant = new Plant(context) { Motor = new Motor(context) { Speed = 5 } };
        var motorProperty = plant.TryGetRegisteredSubject()!.TryGetProperty(nameof(Plant.Motor))!;
        var speedProperty = motorProperty.Children.Single().Subject!.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Motor.Speed))!;
        var fluent = new TestFluent().Set(p => p.Motor.Speed, new TestMapping("CONFIGURED"));

        // Act
        var found = fluent.TryGetMapping(speedProperty, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("CONFIGURED", mapping!.Value);
    }

    [Fact]
    public void WhenPropertyNotConfigured_ThenTryGetMappingReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var plant = new Plant(context) { Motor = new Motor(context) };
        var motorProperty = plant.TryGetRegisteredSubject()!.TryGetProperty(nameof(Plant.Motor))!;
        var speedProperty = motorProperty.Children.Single().Subject!.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Motor.Speed))!;

        // Act
        var found = new TestFluent().TryGetMapping(speedProperty, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
```

**Step 2: Run to verify failure**

Expected: COMPILE FAIL.

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.Connectors/Mapping/FluentPropertyMapperBase.cs
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Base class for connector-specific fluent property mappers. Stores per-property mappings
/// keyed by the property-name path from the root subject (e.g. "Motor.Speed"). Subclasses add
/// type-safe builder methods that ultimately call <see cref="SetMapping"/>.
/// </summary>
/// <remarks>Pattern mirrors <c>FluentOpcUaNodeMapper&lt;T&gt;</c>.</remarks>
public abstract class FluentPropertyMapperBase<TSubject, TMapping> : IPropertyMapper<TMapping>
{
    private readonly ConcurrentDictionary<string, TMapping> _mappings = new();

    /// <summary>Stores a mapping for the property identified by <paramref name="selector"/>.</summary>
    protected void SetMapping<TValue>(Expression<Func<TSubject, TValue>> selector, TMapping mapping)
    {
        var path = GetPathFromExpression(selector.Body);
        _mappings[path] = mapping;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        var path = GetPathFromProperty(property);
        if (path is not null && _mappings.TryGetValue(path, out var stored) && stored is not null)
        {
            mapping = stored;
            return true;
        }
        mapping = default;
        return false;
    }

    private static string GetPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;
        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }
        return string.Join(".", parts);
    }

    private static string GetPathFromProperty(RegisteredSubjectProperty property)
    {
        // Walk up via Subject.Parent (RegisteredSubjectParent).
        // If the API differs, mirror FluentOpcUaNodeMapper.GetPropertyPath(RegisteredSubjectProperty).
        var parts = new List<string> { property.Name };
        var subject = property.Subject;
        while (subject.Parent is { } parent)
        {
            parts.Insert(0, parent.PropertyName);
            subject = parent.Subject.TryGetRegisteredSubject() ?? subject;
            if (parent.Subject is null) break;
        }
        return string.Join(".", parts);
    }
}
```

**Step 4: Run to verify pass**

Expected: PASS (2 tests). If the property-path-from-property logic doesn't match the registered subject API, check `FluentOpcUaNodeMapper.cs` for the working implementation and copy that helper.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/FluentPropertyMapperBase.cs \
        src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentPropertyMapperBaseTests.cs
git commit -m "feat(connectors): add FluentPropertyMapperBase<TSubject, TMapping>"
```

### Task 7: Phase 1 verification

**Step 1: Run the full Connectors test suite**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`

Expected: all existing tests still pass, plus 8 new tests from Tasks 1, 4, 5, 6.

**Step 2: Build the full solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: SUCCESS. (No existing connectors have been touched yet, so this just confirms the new primitives don't break anything downstream.)

**Step 3: If anything fails, fix before continuing.** Phase 1 must be green before starting Phase 2.

---

## Phase 2: WebSocket migration

**Why WebSocket first:** simplest of the three (no per-property fields, no reverse lookup). Validates the foundation with minimal complexity.

**Pre-flight reading:**
- `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs:83` (current `PathProvider?` property)
- `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs:79` (same on server)
- `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs:310-322` (filter usage)

### Task 8: Add `WebSocketPropertyMapping` empty record

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPropertyMapping.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPropertyMappingTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPropertyMappingTests.cs
using Namotion.Interceptor.WebSocket.Mapping;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Mapping;

public class WebSocketPropertyMappingTests
{
    [Fact]
    public void WhenMergingTwoEmptyMappings_ThenReturnsEmptyMapping()
    {
        // Arrange & Act
        var merged = WebSocketPropertyMapping.Merge(new(), new());

        // Assert
        Assert.NotNull(merged);
    }
}
```

**Step 2: Run to verify failure**

Expected: COMPILE FAIL.

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPropertyMapping.cs
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.WebSocket.Mapping;

/// <summary>
/// Connector-specific mapping for a property exposed over WebSocket. Currently holds no fields
/// (inclusion is signaled by <see cref="IPropertyMapper{TMapping}.TryGetMapping"/> returning true).
/// Reserved as the extension point for future per-property fields (e.g. per-property batch sizes).
/// </summary>
public sealed record WebSocketPropertyMapping() : IPropertyMapping<WebSocketPropertyMapping>
{
    public static WebSocketPropertyMapping Merge(WebSocketPropertyMapping over, WebSocketPropertyMapping fallback)
        => over;
}
```

**Step 4: Run to verify pass**

Expected: PASS.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPropertyMapping.cs \
        src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPropertyMappingTests.cs
git commit -m "feat(websocket): add WebSocketPropertyMapping placeholder record"
```

### Task 9: Add WebSocket path-provider adapter

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPathProviderPropertyMapper.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPathProviderPropertyMapperTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPathProviderPropertyMapperTests.cs
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.WebSocket.Mapping;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Mapping;

public class WebSocketPathProviderPropertyMapperTests
{
    [InterceptorSubject]
    public partial class Sample { public partial string Name { get; set; } }

    [Fact]
    public void WhenPathProviderIncludes_ThenMapperReturnsEmptyMapping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sample = new Sample(context);
        var prop = sample.TryGetRegisteredSubject()!.TryGetProperty(nameof(Sample.Name))!;
        var mapper = new WebSocketPathProviderPropertyMapper(DefaultPathProvider.Instance);

        // Act
        var found = mapper.TryGetMapping(prop, out var mapping);

        // Assert
        Assert.True(found);
        Assert.NotNull(mapping);
    }
}
```

**Step 2: Run to verify failure**

Expected: COMPILE FAIL.

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPathProviderPropertyMapper.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Mapping;

/// <summary>
/// Adapter that wraps an <see cref="IPathProvider"/> for WebSocket inclusion filtering.
/// Returns an empty <see cref="WebSocketPropertyMapping"/> when the property is included,
/// <c>null</c> otherwise.
/// </summary>
public class WebSocketPathProviderPropertyMapper : DelegatePropertyMapper<WebSocketPropertyMapping>
{
    public WebSocketPathProviderPropertyMapper(IPathProvider pathProvider)
        : base(property => pathProvider.IsPropertyIncluded(property) ? new WebSocketPropertyMapping() : null) { }
}
```

**Step 4: Run to verify pass**

Expected: PASS.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Mapping/WebSocketPathProviderPropertyMapper.cs \
        src/Namotion.Interceptor.WebSocket.Tests/Mapping/WebSocketPathProviderPropertyMapperTests.cs
git commit -m "feat(websocket): add WebSocketPathProviderPropertyMapper adapter"
```

### Task 10: Update `WebSocketClientConfiguration` and `WebSocketServerConfiguration`

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs`

**Step 1: Read both configuration files**

Locate the `public PathProviderBase? PathProvider { get; set; }` property in each.

**Step 2: Replace with `Mapper`**

In each file, replace:

```csharp
public PathProviderBase? PathProvider { get; set; }
```

with:

```csharp
public IPropertyMapper<WebSocketPropertyMapping>? Mapper { get; set; }
    = new WebSocketPathProviderPropertyMapper(DefaultPathProvider.Instance);
```

Add `using Namotion.Interceptor.Connectors.Mapping;` and `using Namotion.Interceptor.WebSocket.Mapping;` to the top.

Note: WebSocket's path provider was nullable today, so the default is the simplest one (`DefaultPathProvider`) wrapped via the adapter. This preserves the "include everything" default behavior.

**Step 3: Build (will fail — consumers in client source still reference PathProvider)**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`

Expected: COMPILE FAIL. Continue to Task 11.

---

### Task 11: Update WebSocket client/server sources to use the mapper

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs` (or whatever the server file is named)

**Step 1: Find all `PathProvider` usages**

Run: `grep -n "PathProvider\|_pathProvider" src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs src/Namotion.Interceptor.WebSocket/Server/*.cs`

Each call site is one of:
- `_pathProvider?.IsPropertyIncluded(property)` — replace with `_mapper.TryGetMapping(property, out _)` (we don't care about the mapping value, just inclusion)
- `_configuration.PathProvider` — replace with `_configuration.Mapper`

**Step 2: Make the replacements**

For each `IsPropertyIncluded`-style call, the new form is:

```csharp
// Old: if (_pathProvider?.IsPropertyIncluded(property) != false) { ... }
// New:
if (_configuration.Mapper is null || _configuration.Mapper.TryGetMapping(property, out _)) { ... }
```

Or, if you can require `Mapper` non-null (it has a default now), simplify to:

```csharp
if (_configuration.Mapper.TryGetMapping(property, out _)) { ... }
```

(Make `Mapper` non-nullable in the configuration — it now has a default — and remove the null checks here.)

**Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`

Expected: SUCCESS.

---

### Task 12: Update WebSocket tests for the new API

**Files:**
- Modify: any test in `src/Namotion.Interceptor.WebSocket.Tests/` that fails to compile.

**Step 1: Identify failures**

Run: `dotnet build src/Namotion.Interceptor.WebSocket.Tests 2>&1 | grep error`

**Step 2: Fix each test**

- Old: `new WebSocketClientConfiguration { PathProvider = ... }` → New: `new WebSocketClientConfiguration { Mapper = new WebSocketPathProviderPropertyMapper(...) }` or omit Mapper to use the default.
- Old: `new WebSocketServerConfiguration { PathProvider = ... }` → analogous.

**Step 3: Build and test**

```bash
dotnet build src/Namotion.Interceptor.WebSocket.Tests
dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "Category!=Integration"
```

Expected: PASS.

**Step 4: Commit Tasks 10-12 atomically**

```bash
git add src/Namotion.Interceptor.WebSocket/ src/Namotion.Interceptor.WebSocket.Tests/
git commit -m "$(cat <<'EOF'
refactor(websocket): migrate to IPropertyMapper<WebSocketPropertyMapping>

Replace PathProvider with Mapper on client/server configurations, defaulting
to WebSocketPathProviderPropertyMapper wrapping DefaultPathProvider. The mapper
is consulted only for inclusion filtering today; the placeholder mapping record
gives WebSocket a per-property extension point for future fields.
EOF
)"
```

### Task 13: Phase 2 verification

**Step 1: Full build**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: SUCCESS.

**Step 2: Full test (non-integration)**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS. WebSocket has 95 tests; verify they still pass.

---

## Phase 3: OPC UA migration (rename + consolidation)

**Why next:** OPC UA's existing `IOpcUaNodeMapper` *is* the design's reference pattern. Migrating it is mostly a rename — both `IOpcUaNodeMapper` → `IPropertyMapper<OpcUaPropertyMapping>` and `OpcUaNodeConfiguration` → `OpcUaPropertyMapping`. The four concrete mappers (`AttributeOpcUaNodeMapper`, `PathProviderOpcUaNodeMapper`, `FluentOpcUaNodeMapper`, `CompositeNodeMapper`) become thin subclasses or get replaced by the generic primitives.

**Pre-flight reading:**
- All files in `src/Namotion.Interceptor.OpcUa/Mapping/`
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs:11-13`
- `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs:11-15`

### Task 14: Rename `OpcUaNodeConfiguration` to `OpcUaPropertyMapping` and add `Merge`

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeConfiguration.cs` → `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaPropertyMapping.cs`

**Step 1: Rename the file**

```bash
git mv src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeConfiguration.cs \
       src/Namotion.Interceptor.OpcUa/Mapping/OpcUaPropertyMapping.cs
```

**Step 2: Rename the type**

In the new file, rename the record from `OpcUaNodeConfiguration` to `OpcUaPropertyMapping`. Add the `IPropertyMapping<OpcUaPropertyMapping>` interface implementation and a static `Merge` method.

The existing `WithFallback(other)` instance method already implements the merge logic — extract it into the static:

```csharp
// inside the record, after the existing WithFallback method:
public static OpcUaPropertyMapping Merge(OpcUaPropertyMapping over, OpcUaPropertyMapping fallback)
    => over.WithFallback(fallback);
```

Keep `WithFallback` for internal callers (it's used by the existing `CompositeNodeMapper`).

**Step 3: Build (will fail — all consumers reference `OpcUaNodeConfiguration`)**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`

Expected: COMPILE FAIL with many "OpcUaNodeConfiguration not found" errors.

**Step 4: Do a global rename within the OPC UA library**

Use Edit's `replace_all` on each file that references `OpcUaNodeConfiguration`. Find them with:

```bash
grep -rln "OpcUaNodeConfiguration" src/Namotion.Interceptor.OpcUa/ src/HomeBlaze/HomeBlaze.OpcUa/ src/Namotion.Interceptor.OpcUa.Tests/
```

For each file, replace `OpcUaNodeConfiguration` with `OpcUaPropertyMapping`. The verified-API snapshot in `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt` will need a refresh in Task 21 — leave it for now.

**Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`

Expected: SUCCESS.

**Step 6: Commit (intentionally large rename commit)**

```bash
git add src/Namotion.Interceptor.OpcUa/ src/HomeBlaze/HomeBlaze.OpcUa/
git commit -m "refactor(opcua): rename OpcUaNodeConfiguration to OpcUaPropertyMapping"
```

### Task 15: Rename `IOpcUaNodeMapper` to be alias of `IPropertyMapper<OpcUaPropertyMapping>` + reverse

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Mapping/IOpcUaNodeMapper.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaLookupKey.cs`

**Step 1: Add `OpcUaLookupKey`**

```csharp
// src/Namotion.Interceptor.OpcUa/Mapping/OpcUaLookupKey.cs
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Lookup key for OPC UA reverse mapping (node reference to property). Bundled as a record
/// struct so additional fields can be added later without breaking callers.
/// </summary>
public readonly record struct OpcUaLookupKey(ReferenceDescription Reference, ISession Session);
```

**Step 2: Remove `IOpcUaNodeMapper.cs`; consumers switch to the generic interface**

Delete `src/Namotion.Interceptor.OpcUa/Mapping/IOpcUaNodeMapper.cs`. The consumers' new signature is `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>`.

**Step 3: Find every `IOpcUaNodeMapper` consumer**

```bash
grep -rln "IOpcUaNodeMapper" src/Namotion.Interceptor.OpcUa/ src/HomeBlaze/HomeBlaze.OpcUa/ src/Namotion.Interceptor.OpcUa.Tests/
```

For each match:
- Type references: replace `IOpcUaNodeMapper` with `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` (or `IPropertyMapper<OpcUaPropertyMapping>` for places that don't use the reverse).
- Method name on consumers: `TryGetNodeConfiguration(property)` → `TryGetMapping(property, out var mapping)`. This means rewriting consumer call sites from `var config = mapper.TryGetNodeConfiguration(property); if (config is null) continue;` to `if (!mapper.TryGetMapping(property, out var mapping)) continue;`.
- Reverse method: `TryGetPropertyAsync(subject, nodeReference, session, ct)` → `TryGetPropertyAsync(subject, new OpcUaLookupKey(nodeReference, session), ct)`. The return type changes from `Task<RegisteredSubjectProperty?>` to `ValueTask<RegisteredSubjectProperty?>`; wrap return values in `new ValueTask<RegisteredSubjectProperty?>(...)` if needed.

**Step 4: Update the four concrete mappers**

Each existing concrete mapper changes its implementation:

`AttributeOpcUaNodeMapper`:
- Remove `: IOpcUaNodeMapper`, add `: IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>`.
- Rename `TryGetNodeConfiguration` to `TryGetMapping` and change signature to `(property, out mapping)`.
- Rewrite reverse to take `OpcUaLookupKey key` instead of `ReferenceDescription nodeReference, ISession session`. Body uses `key.Reference` and `key.Session`.

`PathProviderOpcUaNodeMapper`: same shape changes.

`FluentOpcUaNodeMapper<T>`: same shape changes plus `: IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>`.

`CompositeNodeMapper`: **delete this file**. Consumers replace with `new CompositePropertyMapper<OpcUaPropertyMapping>(...)`. The default-merge ctor on the generic uses `OpcUaPropertyMapping.Merge` (added in Task 14), which calls `WithFallback`. Behavior preserved.

For places that need an OPC UA-specific composite supporting reverse (because `CompositePropertyMapper<TMapping>` is forward-only, but OPC UA's reverse traversal needs the existing reverse-iterate behavior), keep a thin `OpcUaCompositeReverseMapper` wrapper:

```csharp
// src/Namotion.Interceptor.OpcUa/Mapping/OpcUaCompositeReverseMapper.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Composite that adds OPC UA-specific reverse lookup (later mappers win, reverse-iterate for
/// early return) on top of <see cref="CompositePropertyMapper{TMapping}"/>'s forward composition.
/// </summary>
public sealed class OpcUaCompositeReverseMapper
    : IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    private readonly CompositePropertyMapper<OpcUaPropertyMapping> _forward;
    private readonly IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] _mappers;

    public OpcUaCompositeReverseMapper(
        params IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] mappers)
    {
        _mappers = mappers;
        _forward = new CompositePropertyMapper<OpcUaPropertyMapping>(mappers.Cast<IPropertyMapper<OpcUaPropertyMapping>>().ToArray());
    }

    public bool TryGetMapping(RegisteredSubjectProperty property, out OpcUaPropertyMapping? mapping)
        => _forward.TryGetMapping(property, out mapping);

    public async ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject, OpcUaLookupKey key, CancellationToken cancellationToken)
    {
        for (var i = _mappers.Length - 1; i >= 0; i--)
        {
            var found = await _mappers[i].TryGetPropertyAsync(rootSubject, key, cancellationToken)
                .ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}
```

**Step 5: Update both OPC UA configurations' defaults to use the new types**

`OpcUaClientConfiguration.cs:11-13` and `OpcUaServerConfiguration.cs` similarly:

```csharp
private static readonly IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey> DefaultNodeMapper =
    new OpcUaCompositeReverseMapper(
        new PathProviderOpcUaNodeMapper(new AttributeBasedPathProvider(OpcUaConstants.DefaultConnectorName)),
        new AttributeOpcUaNodeMapper());

public IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey> NodeMapper { get; set; } = DefaultNodeMapper;
```

**Step 6: Build the OPC UA project**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`

Expected: SUCCESS. If failures persist, audit each `IOpcUaNodeMapper` reference and ensure it's been replaced.

**Step 7: Build the OPC UA tests**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests`

Expected: probably some failures from tests instantiating `CompositeNodeMapper` directly or mocking `IOpcUaNodeMapper`. Fix in Task 16.

### Task 16: Update OPC UA tests for the new types

**Files:**
- Modify: any test referencing `IOpcUaNodeMapper`, `CompositeNodeMapper`, `OpcUaNodeConfiguration`, or `TryGetNodeConfiguration` that fails to compile.

**Step 1: Identify failures**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests 2>&1 | grep error`

**Step 2: Mechanical replacements**

For each error:
- `CompositeNodeMapper` → `OpcUaCompositeReverseMapper` (if reverse needed) or `CompositePropertyMapper<OpcUaPropertyMapping>` (forward only)
- `IOpcUaNodeMapper` → `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>`
- `TryGetNodeConfiguration(p)` → `TryGetMapping(p, out var m)`
- `TryGetPropertyAsync(subject, nodeRef, session, ct)` → `TryGetPropertyAsync(subject, new OpcUaLookupKey(nodeRef, session), ct)`
- Mock setups: update to the new interface signatures.

**Step 3: Build and test**

```bash
dotnet build src/Namotion.Interceptor.OpcUa.Tests
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"
```

Expected: PASS (228 tests).

**Step 4: Refresh the public-API snapshot**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~VerifyChecksTests" --no-build
```

If `VerifyChecksTests.PublicApi` fails (expected — the API surface changed), inspect the `*.received.txt` next to the verified file. If the diff matches expected changes (renamed types, new types), accept the snapshot:

```bash
cp src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Rerun the verify test to confirm green.

**Step 5: Commit Tasks 15-16 atomically**

```bash
git add src/Namotion.Interceptor.OpcUa/ src/Namotion.Interceptor.OpcUa.Tests/ src/HomeBlaze/HomeBlaze.OpcUa/
git commit -m "$(cat <<'EOF'
refactor(opcua): migrate to IPropertyMapper<OpcUaPropertyMapping>

Replace IOpcUaNodeMapper with the generic IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>.
TryGetNodeConfiguration becomes TryGetMapping; reverse takes a single OpcUaLookupKey record-struct
bundling ReferenceDescription and ISession. CompositeNodeMapper collapses into a small
OpcUaCompositeReverseMapper that wraps the generic CompositePropertyMapper for forward composition
and keeps OPC UA's reverse-iterate semantics for the reverse path. Defaults in client/server
configurations preserved (PathProvider+Attribute composite).
EOF
)"
```

### Task 17: Phase 3 verification

**Step 1: Full build**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: SUCCESS.

**Step 2: Full test (non-integration)**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

---

## Phase 4: MQTT migration

**Why last (of the existing connectors):** introduces new fields (`Topic`, `QoS`, `Retain`) and requires both forward and reverse mappers. Foundation and patterns are already proven via WebSocket and OPC UA migrations.

**Pre-flight reading:**
- `src/Namotion.Interceptor.Mqtt/Client/MqttClientConfiguration.cs:56` (current `PathProvider`)
- `src/Namotion.Interceptor.Mqtt/Server/MqttServerConfiguration.cs:37` (same on server)
- `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs:381,416-437` (topic generation and reverse lookup)
- `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs:90-92,426` (server path-provider usage)

### Task 18: Add `MqttPropertyMapping`, `MqttLookupKey`, and attribute mapper

**Files:**
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttPropertyMapping.cs`
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttLookupKey.cs`
- Create: `src/Namotion.Interceptor.Mqtt/Attributes/MqttTopicAttribute.cs` (new; analogous to `[AdsVariable]`)
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttAttributePropertyMapper.cs`
- Test: `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPropertyMappingTests.cs`

**Step 1: Define the mapping record**

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttPropertyMapping.cs
using MQTTnet.Protocol;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Per-property MQTT configuration. All fields nullable for partial composition; consumers fall
/// back to global defaults on the <c>MqttClientConfiguration</c> / <c>MqttServerConfiguration</c>.
/// </summary>
public sealed record MqttPropertyMapping(
    string? Topic = null,
    MqttQualityOfServiceLevel? QualityOfService = null,
    bool? Retain = null)
    : IPropertyMapping<MqttPropertyMapping>
{
    public static MqttPropertyMapping Merge(MqttPropertyMapping over, MqttPropertyMapping fallback) => new(
        Topic:            over.Topic            ?? fallback.Topic,
        QualityOfService: over.QualityOfService ?? fallback.QualityOfService,
        Retain:           over.Retain           ?? fallback.Retain);
}
```

**Step 2: Define the lookup key**

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttLookupKey.cs
namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// MQTT reverse-lookup key. Currently just the topic; declared as a record struct so additional
/// fields (e.g. user properties from MQTT 5) can be added later without breaking callers.
/// </summary>
public readonly record struct MqttLookupKey(string Topic);
```

**Step 3: Define the attribute**

```csharp
// src/Namotion.Interceptor.Mqtt/Attributes/MqttTopicAttribute.cs
using MQTTnet.Protocol;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Mqtt.Attributes;

/// <summary>Maps a property to an MQTT topic and (optionally) per-topic QoS and retain.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MqttTopicAttribute : PathAttribute
{
    public MqttTopicAttribute(string topic, string? connectorName = null)
        : base(connectorName ?? MqttConstants.DefaultConnectorName, topic) { }

    public string Topic => Path;

    /// <summary>Per-topic QoS override. If unset, falls back to <c>MqttClientConfiguration.DefaultQualityOfService</c>.</summary>
    public MqttQualityOfServiceLevel QualityOfService { get; init; } = (MqttQualityOfServiceLevel)(-1);

    /// <summary>Per-topic retain override. If unset, falls back to <c>MqttClientConfiguration.UseRetainedMessages</c>.</summary>
    public bool Retain { get; init; } = false;
    public bool RetainSet { get; init; } = false;  // companion flag because bool can't be nullable on attributes

    public MqttPropertyMapping ToMapping() => new(
        Topic: Topic,
        QualityOfService: (int)QualityOfService == -1 ? null : QualityOfService,
        Retain: RetainSet ? Retain : null);
}
```

(`MqttConstants.DefaultConnectorName` may need to be added if it doesn't exist — check `src/Namotion.Interceptor.Mqtt/MqttConstants.cs`. Use `"mqtt"` as the default value.)

**Step 4: Define the attribute mapper**

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttAttributePropertyMapper.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

public class MqttAttributePropertyMapper : DelegatePropertyMapper<MqttPropertyMapping>
{
    public MqttAttributePropertyMapper(string? connectorName = null)
        : base(property => GetMapping(property, connectorName ?? MqttConstants.DefaultConnectorName)) { }

    private static MqttPropertyMapping? GetMapping(RegisteredSubjectProperty property, string connectorName)
    {
        var attribute = property.ReflectionAttributes
            .OfType<MqttTopicAttribute>()
            .FirstOrDefault(a => a.Name == connectorName);
        return attribute?.ToMapping();
    }
}
```

**Step 5: Write a test**

```csharp
// src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPropertyMappingTests.cs
using MQTTnet.Protocol;
using Namotion.Interceptor.Mqtt.Mapping;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttPropertyMappingTests
{
    [Fact]
    public void WhenMergingPartialMappings_ThenOverridePrecedesFallbackPerField()
    {
        // Arrange
        var fallback = new MqttPropertyMapping(
            Topic: "fallback/topic", QualityOfService: MqttQualityOfServiceLevel.AtMostOnce, Retain: false);
        var over = new MqttPropertyMapping(Topic: "override/topic", QualityOfService: null, Retain: true);

        // Act
        var merged = MqttPropertyMapping.Merge(over, fallback);

        // Assert
        Assert.Equal("override/topic", merged.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtMostOnce, merged.QualityOfService);
        Assert.True(merged.Retain);
    }
}
```

**Step 6: Run, verify pass, commit**

```bash
dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~MqttPropertyMappingTests"
git add src/Namotion.Interceptor.Mqtt/Mapping/ \
        src/Namotion.Interceptor.Mqtt/Attributes/MqttTopicAttribute.cs \
        src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPropertyMappingTests.cs
git commit -m "feat(mqtt): add MqttPropertyMapping, MqttTopicAttribute, MqttAttributePropertyMapper"
```

### Task 19: Add MQTT path-provider adapter with reverse lookup

**Files:**
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttPathProviderPropertyMapper.cs`
- Test: `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPathProviderPropertyMapperTests.cs`

**Step 1: Write the failing test**

(Cover both forward — returns mapping with `Topic` set — and reverse — given topic string, returns property.)

```csharp
// src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPathProviderPropertyMapperTests.cs
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttPathProviderPropertyMapperTests
{
    [InterceptorSubject]
    public partial class Sensor
    {
        [Path("mqtt", "temperature")]
        public partial double Temp { get; set; }
    }

    [Fact]
    public void WhenForwardLookup_ThenReturnsMappingWithTopic()
    {
        // Arrange
        var ctx = InterceptorSubjectContext.Create().WithRegistry();
        var sensor = new Sensor(ctx);
        var prop = sensor.TryGetRegisteredSubject()!.TryGetProperty(nameof(Sensor.Temp))!;
        var mapper = new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider("mqtt", '/'));

        // Act
        mapper.TryGetMapping(prop, out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("temperature", mapping.Topic);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenReturnsMatchingProperty()
    {
        // Arrange
        var ctx = InterceptorSubjectContext.Create().WithRegistry();
        var sensor = new Sensor(ctx);
        var registered = sensor.TryGetRegisteredSubject()!;
        var mapper = new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider("mqtt", '/'));

        // Act
        var found = await mapper.TryGetPropertyAsync(registered, new MqttLookupKey("temperature"), CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(nameof(Sensor.Temp), found.Name);
    }
}
```

**Step 2: Run to verify failure, then implement**

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttPathProviderPropertyMapper.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Adapter that wraps an <see cref="PathProviderBase"/> for MQTT forward mapping (path → topic)
/// and reverse mapping (topic → property).
/// </summary>
public class MqttPathProviderPropertyMapper
    : DelegatePropertyMapper<MqttPropertyMapping>,
      IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly PathProviderBase _pathProvider;

    public MqttPathProviderPropertyMapper(PathProviderBase pathProvider)
        : base(property => GetMapping(property, pathProvider))
    {
        _pathProvider = pathProvider;
    }

    private static MqttPropertyMapping? GetMapping(RegisteredSubjectProperty property, PathProviderBase pathProvider)
    {
        if (!pathProvider.IsPropertyIncluded(property))
            return null;
        var topic = property.TryGetPath(pathProvider, rootSubject: null);
        return topic is null ? null : new MqttPropertyMapping(Topic: topic);
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject, MqttLookupKey key, CancellationToken cancellationToken)
    {
        var result = rootSubject.Subject.TryGetPropertyFromPath(_pathProvider, key.Topic);
        return new ValueTask<RegisteredSubjectProperty?>(result?.Property);
    }
}
```

**Step 3: Build, test, commit**

```bash
dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~MqttPathProviderPropertyMapperTests"
git add src/Namotion.Interceptor.Mqtt/Mapping/MqttPathProviderPropertyMapper.cs \
        src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttPathProviderPropertyMapperTests.cs
git commit -m "feat(mqtt): add MqttPathProviderPropertyMapper with forward + reverse lookup"
```

### Task 20: Add MQTT fluent mapper

**Files:**
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMappingBuilder.cs`
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyMapper.cs`

**Step 1: Build the builder + fluent mapper**

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMappingBuilder.cs
using MQTTnet.Protocol;

namespace Namotion.Interceptor.Mqtt.Mapping;

public sealed class MqttFluentMappingBuilder
{
    private string? _topic;
    private MqttQualityOfServiceLevel? _qos;
    private bool? _retain;

    public MqttFluentMappingBuilder WithTopic(string topic)                      { _topic = topic; return this; }
    public MqttFluentMappingBuilder WithQualityOfService(MqttQualityOfServiceLevel qos) { _qos = qos; return this; }
    public MqttFluentMappingBuilder WithRetain(bool retain)                       { _retain = retain; return this; }

    internal MqttPropertyMapping Build() => new(_topic, _qos, _retain);
}
```

```csharp
// src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyMapper.cs
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

public class MqttFluentPropertyMapper<TSubject> : FluentPropertyMapperBase<TSubject, MqttPropertyMapping>
{
    public MqttFluentPropertyMapper<TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<MqttFluentMappingBuilder> configure)
    {
        var builder = new MqttFluentMappingBuilder();
        configure(builder);
        SetMapping(selector, builder.Build());
        return this;
    }
}
```

**Step 2: Add a quick test, build, commit**

```csharp
// src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentPropertyMapperTests.cs
// (analogous to AdsFluentPropertyMapperTests; verify per-topic QoS is applied)
```

```bash
dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~MqttFluentPropertyMapperTests"
git add src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMappingBuilder.cs \
        src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyMapper.cs \
        src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentPropertyMapperTests.cs
git commit -m "feat(mqtt): add MqttFluentPropertyMapper<TSubject>"
```

### Task 21: Update MQTT configurations to use `Mapper` instead of `PathProvider`

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttClientConfiguration.cs:56`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttServerConfiguration.cs:37`

**Step 1: Replace `required PathProviderBase PathProvider` with a defaulted `Mapper`**

In both files:

```csharp
// Old: public required PathProviderBase PathProvider { get; init; }
private static readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> DefaultMapper =
    new MqttCompositeReverseMapper(
        new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider(MqttConstants.DefaultConnectorName, '/')),
        new MqttAttributePropertyMapper());

public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> Mapper { get; set; } = DefaultMapper;
```

Add a small `MqttCompositeReverseMapper` analogous to `OpcUaCompositeReverseMapper` in `src/Namotion.Interceptor.Mqtt/Mapping/MqttCompositeReverseMapper.cs` (same shape; reverse-iterate for first match, forward via `CompositePropertyMapper<MqttPropertyMapping>`).

Update `Validate()` accordingly: remove `PathProvider`-null checks, add a similar check on `Mapper`.

**Step 2: Build (will fail downstream)**

Expected: COMPILE FAIL in `MqttSubjectClientSource` / `MqttSubjectServer`.

---

### Task 22: Update MQTT client/server sources to use the mapper

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`

**Step 1: Find usages**

```bash
grep -n "PathProvider\|TryGetPath\|TryGetPropertyFromPath\|IsPropertyIncluded" \
    src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs \
    src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs
```

**Step 2: Rewrite each**

- For topic computation (forward, e.g. line 416-437): replace `property.TryGetPath(_configuration.PathProvider, _subject)` with `_configuration.Mapper.TryGetMapping(property, out var mapping)` then use `mapping?.Topic`. Use `mapping?.QualityOfService ?? _configuration.DefaultQualityOfService` for the QoS at publish time. Same for retain.
- For property filtering (e.g. line 381 `Where(p => !p.CanContainSubjects)`): combine with `Mapper.TryGetMapping` returning true.
- For reverse lookup (`TryGetPropertyForTopic`): replace with `_configuration.Mapper.TryGetPropertyAsync(rootRegistered, new MqttLookupKey(topic), ct)`.

**Step 3: Build**

```bash
dotnet build src/Namotion.Interceptor.Mqtt
```

Expected: SUCCESS.

---

### Task 23: Update MQTT tests for new API

**Step 1-3:** Same shape as Task 12 / Task 18 — fix tests that reference `PathProvider`, build, run, commit Tasks 21-23 together.

```bash
git add src/Namotion.Interceptor.Mqtt/ src/Namotion.Interceptor.Mqtt.Tests/
git commit -m "$(cat <<'EOF'
refactor(mqtt): migrate to IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>

Replace PathProvider with Mapper on client/server configurations, defaulting to a composite of
MqttPathProviderPropertyMapper + MqttAttributePropertyMapper via MqttCompositeReverseMapper.
MqttPropertyMapping enables per-topic QoS and retain overrides via the new [MqttTopic] attribute
or the fluent mapper. Reverse lookup (topic to property) is now a mapper method instead of an
ad-hoc cache lookup inside MqttSubjectClientSource.
EOF
)"
```

### Task 24: Phase 4 verification

**Step 1: Full build + non-integration tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass.

---

## Phase 5: Documentation

### Task 25: Update `docs/connectors.md` with a new "Property Mappers" section

**Files:**
- Modify: `docs/connectors.md`

**Step 1: Find the right insertion point**

The existing `### Implementing a Source` section starts around line 128. Add a sibling section `### Property Mappers` between the per-source-implementation section and the "Custom Source Example" section.

**Step 2: Write the new content**

Add this before "#### Custom Source Example":

```markdown
### Property Mappers

Connectors translate properties on the subject graph into external-system representations: ADS symbol paths, MQTT topics, OPC UA node IDs, etc. The `IPropertyMapper<TMapping>` abstraction (in `Namotion.Interceptor.Connectors`) provides a uniform way to do this, with each connector defining its own typed `TMapping` record carrying per-property configuration.

#### The interface

```csharp
public interface IPropertyMapper<TMapping>
{
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        out TMapping? mapping);
}

public interface IReversePropertyMapper<TMapping, TKey> : IPropertyMapper<TMapping>
{
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject,
        TKey key,
        CancellationToken cancellationToken);
}
```

A connector that needs reverse lookup (e.g. MQTT receiving messages, OPC UA browsing) implements the reverse interface; one that doesn't (e.g. WebSocket filtering) just implements the forward interface.

#### Built-in mappers

Three generic implementations cover the common patterns:

| Class | Purpose |
|---|---|
| `DelegatePropertyMapper<TMapping>` | Wraps a `Func<RegisteredSubjectProperty, TMapping?>` — used for attribute lookups, path-provider adapters, custom one-offs. |
| `CompositePropertyMapper<TMapping>` | Combines multiple mappers; later mappers' fields override earlier ones via the record's `Merge` method. |
| `FluentPropertyMapperBase<TSubject, TMapping>` | Base class for type-safe per-property configuration; subclasses add `Map(x => x.Foo, b => b.WithBar(...))`-style builders. |

Each connector ships thin wrappers over these generics — for example, MQTT provides `MqttAttributePropertyMapper` (reads `[MqttTopic]`), `MqttPathProviderPropertyMapper` (wraps `IPathProvider`), and `MqttFluentPropertyMapper<TSubject>` (code-based configuration).

#### Composition and defaults

Each connector's configuration object defaults `Mapper` to a static composite combining the path-provider adapter (fallback for properties with `[Path]` attributes) and the attribute mapper (override for properties with connector-specific attributes). Users with `[MqttTopic("sensors/temp")]` or `[Path("mqtt", "sensors/temp")]` properties don't need to set anything; the default composite handles both.

```csharp
// MqttClientConfiguration default (illustrative):
private static readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> DefaultMapper =
    new MqttCompositeReverseMapper(
        new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributePropertyMapper());

public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> Mapper { get; set; } = DefaultMapper;
```

Replacing the default for advanced cases:

```csharp
services.AddMqttSubjectClientSource(
    sp => sp.GetRequiredService<Sensors>(),
    _ => new MqttClientConfiguration {
        BrokerHost = "broker.example.com",
        Mapper = new MqttCompositeReverseMapper(
            new MqttAttributePropertyMapper(),
            new MqttFluentPropertyMapper<Sensors>()
                .Map(s => s.CriticalTemp, b => b
                    .WithTopic("alerts/critical/temp")
                    .WithQualityOfService(MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithRetain(true))),
    });
```

#### Implementing a mapper for a new connector

To add property-mapping support to a new connector, you need four pieces:

1. **A mapping record** describing per-property configuration. All fields nullable so that partial mappings compose:

   ```csharp
   public sealed record FooPropertyMapping(string? Address = null, int? PollMs = null)
       : IPropertyMapping<FooPropertyMapping>
   {
       public static FooPropertyMapping Merge(FooPropertyMapping over, FooPropertyMapping fallback) => new(
           Address: over.Address ?? fallback.Address,
           PollMs:  over.PollMs  ?? fallback.PollMs);
   }
   ```

2. **An attribute** carrying the same fields, with a `ToMapping()` helper that converts the attribute's compile-time values into a runtime mapping:

   ```csharp
   public sealed class FooVariableAttribute : PathAttribute
   {
       public FooVariableAttribute(string address) : base("foo", address) { }
       public string Address => Path;
       public int PollMs { get; init; } = -1;
       public FooPropertyMapping ToMapping() => new(
           Address: Address,
           PollMs:  PollMs == -1 ? null : PollMs);
   }
   ```

3. **Wrapper mappers** for attributes, path provider, and fluent — each is a thin subclass of the matching generic:

   ```csharp
   public class FooAttributePropertyMapper : DelegatePropertyMapper<FooPropertyMapping>
   {
       public FooAttributePropertyMapper(string? connectorName = null)
           : base(p => p.ReflectionAttributes.OfType<FooVariableAttribute>()
               .FirstOrDefault(a => a.Name == (connectorName ?? "foo"))?.ToMapping()) { }
   }

   public class FooPathProviderPropertyMapper : DelegatePropertyMapper<FooPropertyMapping>
   {
       public FooPathProviderPropertyMapper(IPathProvider pathProvider)
           : base(p => pathProvider.IsPropertyIncluded(p)
               ? new FooPropertyMapping(Address: p.TryGetPath(pathProvider, null))
               : null) { }
   }

   public class FooFluentPropertyMapper<TSubject> : FluentPropertyMapperBase<TSubject, FooPropertyMapping>
   {
       public FooFluentPropertyMapper<TSubject> Map<TValue>(
           Expression<Func<TSubject, TValue>> selector,
           Action<FooFluentMappingBuilder> configure)
       { /* see Mqtt or Twin CAT for the builder pattern */ }
   }
   ```

4. **A configuration default** combining the path-provider adapter and attribute mapper:

   ```csharp
   public class FooClientConfiguration
   {
       private static readonly IPropertyMapper<FooPropertyMapping> DefaultMapper =
           new CompositePropertyMapper<FooPropertyMapping>(
               new FooPathProviderPropertyMapper(new AttributeBasedPathProvider("foo")),
               new FooAttributePropertyMapper());

       public IPropertyMapper<FooPropertyMapping> Mapper { get; set; } = DefaultMapper;
       // ... other configuration fields
   }
   ```

If the connector needs reverse lookup, add an `IReversePropertyMapper<FooPropertyMapping, FooLookupKey>` implementation (typically on the path-provider adapter, following `MqttPathProviderPropertyMapper`), and define `FooLookupKey` as a `readonly record struct` so additional fields can be added later without breaking callers.

#### When to use which mapper

- **`[Path("foo", "...")]` cross-protocol attribute** for properties exposed via multiple connectors at the same name (`PathProviderConnectorMapper` handles these).
- **Connector-specific attribute** (`[MqttTopic]`, `[OpcUaNode]`, `[AdsVariable]`) for connector-specific knobs (QoS, sampling interval, cycle time).
- **Fluent mapper** for instance-specific configuration that can't be expressed in attributes (e.g. mapping `Plant.Motor1.Speed` and `Plant.Motor2.Speed` to different external addresses).
- **Composite** when combining the above. Default composite (attribute + path provider) is automatic; manual composites layer fluent overrides on top.
```

**Step 3: Verify the doc renders sensibly**

```bash
# If the project has a Markdown linter, run it. Otherwise manually skim the change.
```

**Step 4: Commit**

```bash
git add docs/connectors.md
git commit -m "docs(connectors): document IPropertyMapper<TMapping> abstraction"
```

### Task 26: Add a small section to per-connector docs

**Files:**
- Modify: `docs/connectors-mqtt.md` (add a "Property Mapper" subsection mentioning `MqttPropertyMapping`, `[MqttTopic]`, and per-topic QoS/Retain)
- Modify: `docs/connectors-opcua.md` (note: `IOpcUaNodeMapper` renamed to `IPropertyMapper<OpcUaPropertyMapping>`; provide a migration note)
- Modify: `docs/connectors-websocket.md` (note: `Mapper` replaces `PathProvider`)

For each: add 1-2 short paragraphs with a code snippet showing the new shape. Keep the per-connector docs focused on the connector-specific aspects; the general mapper concept lives in `docs/connectors.md`.

Commit:

```bash
git add docs/connectors-*.md
git commit -m "docs(connectors): update per-connector docs for property mapper migration"
```

### Task 27: Final mapper-branch verification

**Step 1: Full solution build + non-integration tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: SUCCESS, all tests pass.

**Step 2: Connector integration tests (the ones already targeted in CI)**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests
dotnet test src/Namotion.Interceptor.Mqtt.Tests
dotnet test src/Namotion.Interceptor.WebSocket.Tests
```

Expected: all pass. If integration tests can't run locally (e.g. no Docker), note this for CI verification.

**Step 3: Push the branch**

```bash
git push -u origin feature/property-mapper-abstraction
```

The mapper branch is ready for PR/review. Open the PR against `master`. Once it lands, Phase 6 begins.

---

## Phase 6: TwinCAT migration (after mapper branch merges)

This phase runs on `feature/add-twincat-connector` after rebasing it onto the merged-mapper master.

### Task 28: Rebase TwinCAT branch onto the new master

**Step 1: Confirm mapper branch is merged**

```bash
git fetch origin
git log origin/master --oneline -5
# Expect to see the mapper branch's commits at the tip.
```

**Step 2: Check out the TwinCAT branch**

```bash
git checkout feature/add-twincat-connector
```

**Step 3: Rebase onto the new master**

```bash
git rebase origin/master
```

Expected: most of the existing TwinCAT commits replay cleanly. Conflicts most likely in:
- `src/Namotion.Interceptor.slnx` (if the mapper PR also touched it)
- `docs/connectors.md` (if the mapper PR added the Property Mappers section)
- `docs/twincat.md` (if structure changed)

Resolve as appropriate. Continue rebase to completion.

**Step 4: Force-push the rebased branch**

```bash
git push --force-with-lease
```

Phase 6 implementation begins.

### Task 29: Add `AdsPropertyMapping` record with TDD

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT/Mapping/AdsPropertyMapping.cs`
- Test: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Mapping/AdsPropertyMappingTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Mapping/AdsPropertyMappingTests.cs
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Mapping;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Mapping;

public class AdsPropertyMappingTests
{
    [Fact]
    public void WhenMergingPartialMappings_ThenOverridePrecedesFallbackPerField()
    {
        // Arrange
        var fallback = new AdsPropertyMapping(
            SymbolPath: "fallback.path", ReadMode: AdsReadMode.Polled,
            CycleTime: 100, MaxDelay: 50, Priority: 0);
        var over = new AdsPropertyMapping(
            SymbolPath: "override.path", ReadMode: null,
            CycleTime: 200, MaxDelay: null, Priority: 5);

        // Act
        var merged = AdsPropertyMapping.Merge(over, fallback);

        // Assert
        Assert.Equal("override.path", merged.SymbolPath);
        Assert.Equal(AdsReadMode.Polled, merged.ReadMode);
        Assert.Equal(200, merged.CycleTime);
        Assert.Equal(50, merged.MaxDelay);
        Assert.Equal(5, merged.Priority);
    }
}
```

**Step 2: Run to verify failure, then implement**

```csharp
// src/Namotion.Interceptor.Connectors.TwinCAT/Mapping/AdsPropertyMapping.cs
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.TwinCAT.Client;

namespace Namotion.Interceptor.Connectors.TwinCAT.Mapping;

public sealed record AdsPropertyMapping(
    string? SymbolPath = null,
    AdsReadMode? ReadMode = null,
    int? CycleTime = null,
    int? MaxDelay = null,
    int? Priority = null)
    : IPropertyMapping<AdsPropertyMapping>
{
    public static AdsPropertyMapping Merge(AdsPropertyMapping over, AdsPropertyMapping fallback) => new(
        SymbolPath: over.SymbolPath ?? fallback.SymbolPath,
        ReadMode:   over.ReadMode   ?? fallback.ReadMode,
        CycleTime:  over.CycleTime  ?? fallback.CycleTime,
        MaxDelay:   over.MaxDelay   ?? fallback.MaxDelay,
        Priority:   over.Priority   ?? fallback.Priority);
}
```

**Step 3: Run pass, commit**

```bash
dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "FullyQualifiedName~AdsPropertyMappingTests"
git add src/Namotion.Interceptor.Connectors.TwinCAT/Mapping/AdsPropertyMapping.cs \
        src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Mapping/AdsPropertyMappingTests.cs
git commit -m "feat(twincat): add AdsPropertyMapping record with field-wise merge"
```

### Task 30: Add `AdsVariableAttribute.ToMapping()`, `AdsAttributePropertyMapper`, `AdsPathProviderPropertyMapper`

Pattern identical to Tasks 18-19 for MQTT, with TwinCAT-specific fields. See those tasks for the template; the differences:
- Attribute already exists (`AdsVariableAttribute`) — add a `ToMapping()` method.
- `AdsAttributePropertyMapper` reads `[AdsVariable]` like `MqttAttributePropertyMapper` reads `[MqttTopic]`.
- `AdsPathProviderPropertyMapper` is forward-only (TwinCAT doesn't need reverse via the mapper — its internal symbol cache handles reverse).

Tests verify each in isolation. Commit each separately:

```bash
git commit -m "feat(twincat): add AdsVariableAttribute.ToMapping() conversion helper"
git commit -m "feat(twincat): add AdsAttributePropertyMapper reading [AdsVariable]"
git commit -m "feat(twincat): add AdsPathProviderPropertyMapper adapter"
```

### Task 31: Add `AdsFluentPropertyMapper<TSubject>` with builder

Pattern identical to Task 20 (MQTT). The builder methods are `WithSymbolPath`, `WithCycleTime`, `WithMaxDelay`, `WithReadMode`, `WithPriority`. Commit:

```bash
git commit -m "feat(twincat): add AdsFluentPropertyMapper<TSubject> with type-safe builder"
```

### Task 32: Update `AdsClientConfiguration` to use `Mapper` instead of `PathProvider`

Pattern identical to Task 21 (MQTT). The default is:

```csharp
private static readonly IPropertyMapper<AdsPropertyMapping> DefaultMapper =
    new CompositePropertyMapper<AdsPropertyMapping>(
        new AdsPathProviderPropertyMapper(new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName)),
        new AdsAttributePropertyMapper());

public IPropertyMapper<AdsPropertyMapping> Mapper { get; set; } = DefaultMapper;
```

Remove the existing `required IPathProvider PathProvider` property. The build will fail until Tasks 33-34 are done.

### Task 33: Update `AdsSubjectLoader` to use the mapper (absolute-path aware)

Same as the prior plan's Task 14. The loader walks the subject graph; for each leaf, it calls `_mapper.TryGetMapping(property, out var mapping)`. If the mapping's `SymbolPath` is null, the leaf is skipped. The loader no longer prepends parent segments — the mapper supplies the complete path. Result type changes from `(RegisteredSubjectProperty, string)` to `(RegisteredSubjectProperty, AdsPropertyMapping)`.

This is where absolute symbol paths for nested properties start working: the fluent mapper supplies the full path, and the loader uses it verbatim.

```bash
git commit -m "refactor(twincat): AdsSubjectLoader consumes IPropertyMapper<AdsPropertyMapping>"
```

### Task 34: Update `AdsSubscriptionManager` to read per-property knobs from the mapping

Same as the prior plan's Task 16. `AdsSubscriptionManager.DetermineEffectiveReadModes` stops calling `property.ReflectionAttributes.OfType<AdsVariableAttribute>()` and instead reads from the stored `AdsPropertyMapping`. Each field falls back to the global default on the configuration when the mapping has null:

```csharp
var readMode  = mapping.ReadMode  ?? _configuration.DefaultReadMode;
var cycleTime = mapping.CycleTime ?? _configuration.DefaultCycleTime;
var maxDelay  = mapping.MaxDelay  ?? _configuration.DefaultMaxDelay;
var priority  = mapping.Priority  ?? 0;
```

Plumb the mappings from the loader's output through `RegisterSubscriptions` so the demotion logic has access.

### Task 35: Update `TwinCatSubjectExtensions` to drop `pathProviderName`

Same as the prior plan's Task 17. The simple overload becomes:

```csharp
public static IServiceCollection AddTwinCatSubjectClientSource<TSubject>(
    this IServiceCollection services,
    string host,
    int amsPort = 851,
    string? amsNetId = null)
    where TSubject : IInterceptorSubject
```

(No `pathProviderName` parameter. Users who need multi-protocol mapping go through the full configuration overload and supply an explicit mapper.)

### Task 36: Migrate existing TwinCAT tests

Same as the prior plan's Task 18. Mechanical replacements:
- `new AdsSubjectLoader(pathProvider)` → `new AdsSubjectLoader(mapper)`
- `new AdsClientConfiguration { PathProvider = ... }` → `new AdsClientConfiguration { Mapper = ... }` (or omit; default kicks in)
- `AddTwinCatSubjectClientSource<T>(host, pathProviderName: "ads")` → `AddTwinCatSubjectClientSource<T>(host)`
- Loader output assertion: from `(property, string path)` to `(property, AdsPropertyMapping mapping)` with `mapping.SymbolPath`.

Run all non-integration TwinCAT tests after the changes.

### Task 37: Atomic commit of Tasks 32-36

The intermediate states leave the build broken. Commit Tasks 32-36 together as a single migration commit:

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT/ \
        src/Namotion.Interceptor.Connectors.TwinCAT.Tests/
git commit -m "$(cat <<'EOF'
refactor(twincat): migrate to IPropertyMapper<AdsPropertyMapping>

Replace AdsClientConfiguration.PathProvider with a Mapper property defaulting
to a static composite of AdsPathProviderPropertyMapper + AdsAttributePropertyMapper.
AdsSubjectLoader now consumes mappings directly per leaf, so absolute symbol paths
(from [AdsVariable] or fluent configuration) work for arbitrarily nested properties
without parent-segment concatenation. AdsSubscriptionManager reads ReadMode,
CycleTime, MaxDelay, and Priority from the mapping instead of re-reading the
attribute, so the fluent and path-provider mappers can supply these knobs too.
TwinCatSubjectExtensions drops the pathProviderName parameter on the simple
overload; multi-protocol scenarios go through the full configuration overload.
EOF
)"
```

### Task 38: Add the motivating integration test — absolute paths for nested properties

Same as the prior plan's Task 19. Verifies the fluent mapper supplies absolute paths for `Plant.Motor1.Speed` (etc.) and the loader returns them verbatim with no parent-segment prepending.

```csharp
[Fact]
public void WhenFluentMapperSuppliesAbsolutePaths_ThenLoaderReturnsThemUnmodified()
{
    // Arrange
    var context = InterceptorSubjectContext.Create().WithRegistry();
    var plant = new Plant(context) {
        Motor1 = new Motor(context) { Speed = 10, Torque = 5 },
        Motor2 = new Motor(context) { Speed = 20, Torque = 8 },
    };

    var mapper = new AdsFluentPropertyMapper<Plant>()
        .Map(p => p.Motor1.Speed,  b => b.WithSymbolPath("PRODUCTION.LINE1.M1.Speed"))
        .Map(p => p.Motor1.Torque, b => b.WithSymbolPath("PRODUCTION.LINE1.M1.Torque"))
        .Map(p => p.Motor2.Speed,  b => b.WithSymbolPath("PRODUCTION.LINE1.M2.Speed"))
        .Map(p => p.Motor2.Torque, b => b.WithSymbolPath("PRODUCTION.LINE1.M2.Torque"));

    var loader = new AdsSubjectLoader(mapper);

    // Act
    var leaves = loader.LoadSubjectGraph(plant);

    // Assert — absolute paths preserved verbatim
    var paths = leaves.Select(t => t.Mapping.SymbolPath).ToList();
    Assert.Equal(4, leaves.Count);
    Assert.Contains("PRODUCTION.LINE1.M1.Speed",  paths);
    Assert.Contains("PRODUCTION.LINE1.M1.Torque", paths);
    Assert.Contains("PRODUCTION.LINE1.M2.Speed",  paths);
    Assert.Contains("PRODUCTION.LINE1.M2.Torque", paths);
}
```

Commit:

```bash
git commit -m "test(twincat): absolute symbol paths preserved through nested subjects"
```

### Task 39: Update `docs/twincat.md` to describe the new mapper

Add a short section ("Configuration with property mappers") after the existing setup section showing:
1. The attribute-based default (no change to user code beyond removing `pathProviderName`).
2. The fluent mapper for absolute paths.
3. Combining them via composite.

Refer back to `docs/connectors.md` for the general mapper concept.

Commit:

```bash
git add docs/twincat.md
git commit -m "docs(twincat): document AdsPropertyMapping and fluent mapper for absolute paths"
```

### Task 40: Final TwinCAT-branch verification + push

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
git push --force-with-lease
```

Expected: SUCCESS, all tests pass. The TwinCAT branch is ready for PR.

---

## Out-of-scope follow-ups

Not part of this plan; future work:

1. **Replacing `IPathProvider` in registry/MCP/AspNetCore.** Different concept (graph-coordinate addressing); no benefit from per-connector richness. Stays as-is.
2. **Source generator for record `Merge` methods.** Hand-written merges are small and readable enough that source-gen overhead isn't justified yet.
3. **WebSocket per-property fields.** `WebSocketPropertyMapping` ships empty; fields are added when a real use case appears.
4. **`PropertyMappers` per-connector static helper classes** (`AdsPropertyMappers.Composite(...)` etc.). Optional discoverability sugar; not required for any of the above to work.
5. **Builder-pattern `.AddMapper(...)` DI extension** that appends to a composite default. Setting `Mapper = ...` to replace the default is sufficient and less surprising.
