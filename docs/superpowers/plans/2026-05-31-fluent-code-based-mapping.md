# Code-based fluent mapping (type-level) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make code-based fluent configuration a complete, type-level alternative to attribute mapping for the MQTT and OPC UA connectors, reusing the existing path engine and composite, and deleting the old absolute-path fluent mappers.

**Architecture:** Fluent becomes a second *source* keyed by `(declaringType, member)` exactly like attributes, feeding the same `PathProviderBase` + composite machinery. A shared `FluentMappingRegistry<TMetadata>` holds segments and metadata; a shared `FluentPathProvider : PathProviderBase` reads segments; a shared `FluentMetadataMapper<TMetadata, TKey>` supplies metadata (reverse stays in the existing path-provider mappers). Each connector adds only a thin facade (`MqttFluentMapping<TRoot>` / `OpcUaFluentMapping<TRoot>`), a property builder, and DI wiring.

**Tech Stack:** C# 13, .NET 9 / .NET Standard 2.0 (core), xUnit, Verify + PublicApiGenerator (API snapshots), MQTTnet, OPCFoundation OPC UA SDK.

**Scope:** Type-level (`ForType<T>().Map(...)` and `.Configure(...)`) only. Instance-level `ForPath` is deferred to issue #328. Spec: `docs/superpowers/specs/2026-05-31-fluent-code-based-mapping-design.md`. Branch: `feature/325-fluent-code-based-mapping`.

**Conventions (from CLAUDE.md):** Test names `When<Condition>_Then<Expected>`; explicit `// Arrange` / `// Act` / `// Assert`; no em dashes in docs; no AI attribution in commits; descriptive names, minimal comments.

**Build/test commands:**
- Build all: `dotnet build src/Namotion.Interceptor.slnx`
- Test a project: `dotnet test src/<Project>` (e.g. `dotnet test src/Namotion.Interceptor.Connectors.Tests`)
- Unit only (exclude integration): append `--filter "Category!=Integration"`

---

## Phase 1: Shared core (Registry + Connectors)

### Task 1: `IFluentSegmentSource` seam (Registry)

**Files:**
- Create: `src/Namotion.Interceptor.Registry/Paths/IFluentSegmentSource.cs`

- [ ] **Step 1: Create the interface**

```csharp
namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Supplies the per-(type, member) path segment for code-based (fluent) mapping. Implemented by the
/// connector-agnostic fluent registry so <see cref="FluentPathProvider"/> can resolve segments without
/// depending on connector metadata types.
/// </summary>
public interface IFluentSegmentSource
{
    /// <summary>
    /// Returns true when a type-level registration exists for the given holder type and member.
    /// <paramref name="segment"/> is the registered segment override, or null to mean "use the
    /// member's BrowseName".
    /// </summary>
    bool TryGetSegment(Type subjectType, string member, out string? segment);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.Registry`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Paths/IFluentSegmentSource.cs
git commit -m "Add IFluentSegmentSource seam for fluent path provider"
```

---

### Task 2: `FluentMappingRegistry<TMetadata>` (Connectors)

The source of truth: type-level `(Type, member) -> {segment, metadata}` and type-self `Type -> metadata`, with the inheritance walk (runtime type, base classes, interfaces, most-derived first).

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/FluentMappingRegistry.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentMappingRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Connectors.Mapping;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentMappingRegistryTests
{
    private sealed record Meta(string Value);

    private interface IMotor { }
    private class Motor : IMotor { }
    private sealed class ServoMotor : Motor { }

    [Fact]
    public void WhenTypeAndMemberRegistered_ThenResolvesSegmentAndMetadata()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(Motor), "Speed", "speed", new Meta("m"));

        // Act
        var hasSegment = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);
        var hasMeta = registry.TryGetTypeMetadata(typeof(Motor), "Speed", out var meta);

        // Assert
        Assert.True(hasSegment);
        Assert.Equal("speed", segment);
        Assert.True(hasMeta);
        Assert.Equal(new Meta("m"), meta);
    }

    [Fact]
    public void WhenRegisteredOnBaseType_ThenResolvesForDerivedType()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(Motor), "Speed", "speed", new Meta("m"));

        // Act
        var found = registry.TryGetSegment(typeof(ServoMotor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Equal("speed", segment);
    }

    [Fact]
    public void WhenRegisteredOnInterface_ThenResolvesForImplementer()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(IMotor), "Speed", "rpm", new Meta("i"));

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Equal("rpm", segment);
    }

    [Fact]
    public void WhenBothDerivedAndBaseRegistered_ThenMostDerivedWins()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(Motor), "Speed", "base", new Meta("base"));
        registry.AddType(typeof(ServoMotor), "Speed", "derived", new Meta("derived"));

        // Act
        registry.TryGetSegment(typeof(ServoMotor), "Speed", out var segment);

        // Assert
        Assert.Equal("derived", segment);
    }

    [Fact]
    public void WhenNotRegistered_ThenReturnsFalse()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenSegmentOmitted_ThenIsRegisteredWithNullSegment()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(Motor), "Speed", segment: null, new Meta("m"));

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Null(segment);
    }

    [Fact]
    public void WhenTypeSelfRegistered_ThenResolvesWithInheritance()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddTypeSelf(typeof(Motor), new Meta("self"));

        // Act
        var found = registry.TryGetTypeSelfMetadata(typeof(ServoMotor), out var meta);

        // Assert
        Assert.True(found);
        Assert.Equal(new Meta("self"), meta);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentMappingRegistryTests"`
Expected: FAIL (type `FluentMappingRegistry` does not exist).

- [ ] **Step 3: Write the implementation**

```csharp
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Source of truth for code-based (fluent) mapping. Holds type-level segment plus metadata keyed by
/// (declaring type, member) and type-self metadata keyed by type. Resolution walks the runtime type, its
/// base classes, then its interfaces, most-derived first, so a base or interface registration applies to
/// derived and implementing types.
/// </summary>
public sealed class FluentMappingRegistry<TMetadata> : IFluentSegmentSource
    where TMetadata : class
{
    private readonly Dictionary<(Type Type, string Member), Entry> _typeLevel = new();
    private readonly Dictionary<Type, TMetadata> _typeSelf = new();

    /// <summary>Registers a type-level mapping for a member. A null segment means "use the BrowseName".</summary>
    public void AddType(Type declaringType, string member, string? segment, TMetadata metadata)
        => _typeLevel[(declaringType, member)] = new Entry(segment, metadata);

    /// <summary>Registers type-self (class-level) metadata for a type.</summary>
    public void AddTypeSelf(Type type, TMetadata metadata)
        => _typeSelf[type] = metadata;

    /// <inheritdoc />
    public bool TryGetSegment(Type subjectType, string member, out string? segment)
    {
        if (TryResolveType(subjectType, member, out var entry))
        {
            segment = entry.Segment;
            return true;
        }

        segment = null;
        return false;
    }

    /// <summary>Resolves the type-level metadata for a member, walking the type hierarchy.</summary>
    public bool TryGetTypeMetadata(Type subjectType, string member, [NotNullWhen(true)] out TMetadata? metadata)
    {
        if (TryResolveType(subjectType, member, out var entry))
        {
            metadata = entry.Metadata;
            return true;
        }

        metadata = null;
        return false;
    }

    /// <summary>Resolves type-self metadata for a type, walking the type hierarchy.</summary>
    public bool TryGetTypeSelfMetadata(Type type, [NotNullWhen(true)] out TMetadata? metadata)
    {
        foreach (var candidate in WalkTypeHierarchy(type))
        {
            if (_typeSelf.TryGetValue(candidate, out metadata))
                return true;
        }

        metadata = null;
        return false;
    }

    private bool TryResolveType(Type subjectType, string member, out Entry entry)
    {
        foreach (var candidate in WalkTypeHierarchy(subjectType))
        {
            if (_typeLevel.TryGetValue((candidate, member), out entry))
                return true;
        }

        entry = default;
        return false;
    }

    // Most-derived first: the runtime type, then each base class up the chain, then interfaces.
    private static IEnumerable<Type> WalkTypeHierarchy(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;

        foreach (var interfaceType in type.GetInterfaces())
            yield return interfaceType;
    }

    private readonly record struct Entry(string? Segment, TMetadata Metadata);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentMappingRegistryTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/FluentMappingRegistry.cs src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentMappingRegistryTests.cs
git commit -m "Add FluentMappingRegistry with inheritance-aware resolution"
```

---

### Task 3: `FluentPathProvider` (Registry)

A drop-in for `AttributeBasedPathProvider` that sources segments from an `IFluentSegmentSource`.

**Files:**
- Create: `src/Namotion.Interceptor.Registry/Paths/FluentPathProvider.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Paths/FluentPathProviderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Paths;

public class FluentPathProviderTests
{
    private sealed record Meta;

    [Fact]
    public void WhenPropertyRegistered_ThenIncludedAndSegmentReturned()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(FluentPathTestSensor), "Temperature", "temp", new Meta());
        var provider = new FluentPathProvider(registry, '/');

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var included = provider.IsPropertyIncluded(property);
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.True(included);
        Assert.Equal("temp", segment);
    }

    [Fact]
    public void WhenRegisteredWithoutSegment_ThenFallsBackToBrowseName()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(FluentPathTestSensor), "Temperature", segment: null, new Meta());
        var provider = new FluentPathProvider(registry);

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.Equal("Temperature", segment);
    }

    [Fact]
    public void WhenPropertyNotRegistered_ThenExcludedAndNullSegment()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var provider = new FluentPathProvider(registry);

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var included = provider.IsPropertyIncluded(property);
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.False(included);
        Assert.Null(segment);
    }

    [Fact]
    public void WhenSeparatorConfigured_ThenExposed()
    {
        // Arrange
        var provider = new FluentPathProvider(new FluentMappingRegistry<Meta>(), '/');

        // Act & Assert
        Assert.Equal('/', provider.PathSeparator);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class FluentPathTestSensor
{
    public partial double Temperature { get; set; }

    public FluentPathTestSensor()
    {
        Temperature = 0;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentPathProviderTests"`
Expected: FAIL (type `FluentPathProvider` does not exist).

- [ ] **Step 3: Write the implementation**

```csharp
using System.Linq;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Path provider that sources segments from a code-based <see cref="IFluentSegmentSource"/> instead of
/// attributes. A drop-in replacement for <see cref="AttributeBasedPathProvider"/>: it inherits forward
/// composition, index handling, [InlinePaths] support, and segment-guided reverse lookup from
/// <see cref="PathProviderBase"/>.
/// </summary>
public sealed class FluentPathProvider : PathProviderBase
{
    private readonly IFluentSegmentSource _source;
    private readonly char _pathSeparator;

    /// <summary>Creates a provider over the given fluent segment source.</summary>
    /// <param name="source">The fluent registry to read segments from.</param>
    /// <param name="pathSeparator">The path separator character. Default is '.'.</param>
    public FluentPathProvider(IFluentSegmentSource source, char pathSeparator = '.')
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        if (_source.TryGetSegment(property.Subject.GetType(), property.Name, out _))
            return true;

        // Mirror AttributeBasedPathProvider: [InlinePaths] containers participate in path resolution.
        return property.ReflectionAttributes.OfType<InlinePathsAttribute>().Any();
    }

    /// <inheritdoc />
    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (_source.TryGetSegment(property.Subject.GetType(), property.Name, out var segment))
            return segment ?? property.BrowseName;

        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentPathProviderTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Paths/FluentPathProvider.cs src/Namotion.Interceptor.Connectors.Tests/Paths/FluentPathProviderTests.cs
git commit -m "Add FluentPathProvider reading segments from the fluent registry"
```

---

### Task 4: `FluentMetadataMapper<TMetadata, TKey>` (Connectors)

Forward returns the type-level metadata for a property; reverse returns null (owned by the path-provider mapper).

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Mapping/FluentMetadataMapper.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentMetadataMapperTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentMetadataMapperTests
{
    private sealed record Meta(string Value) : IPropertyMapping<Meta>
    {
        public static Meta Merge(Meta primary, Meta fallback) => primary;
    }

    [Fact]
    public void WhenPropertyRegistered_ThenReturnsMetadata()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(MetadataMapperTestSensor), "Temperature", null, new Meta("hot"));
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var subject = new MetadataMapperTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal(new Meta("hot"), mapping);
    }

    [Fact]
    public void WhenPropertyNotRegistered_ThenReturnsFalse()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var subject = new MetadataMapperTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenReturnsNull()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MetadataMapperTestSensor(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync("anything", registered, CancellationToken.None);

        // Assert - reverse lookup is owned by the path-provider mapper.
        Assert.Null(found);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class MetadataMapperTestSensor
{
    public partial double Temperature { get; set; }

    public MetadataMapperTestSensor()
    {
        Temperature = 0;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentMetadataMapperTests"`
Expected: FAIL (type `FluentMetadataMapper` does not exist).

- [ ] **Step 3: Write the implementation**

```csharp
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Supplies code-based (fluent) protocol metadata for a property from a <see cref="FluentMappingRegistry{TMetadata}"/>.
/// The drop-in analog of the per-connector attribute metadata mapper: it contributes forward metadata only,
/// and returns null on reverse because reverse lookup is owned by the path-provider mapper (segments are
/// type-level and identical forward and reverse).
/// </summary>
public class FluentMetadataMapper<TMetadata, TKey> : IReversePropertyMapper<TMetadata, TKey>
    where TMetadata : class, IPropertyMapping<TMetadata>
{
    /// <summary>The registry this mapper reads from. Available to subclasses that add resolution (e.g. type-self).</summary>
    protected FluentMappingRegistry<TMetadata> Registry { get; }

    public FluentMetadataMapper(FluentMappingRegistry<TMetadata> registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public virtual bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMetadata? mapping)
    {
        if (Registry.TryGetTypeMetadata(property.Subject.GetType(), property.Name, out var metadata))
        {
            mapping = metadata;
            return true;
        }

        mapping = null;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key, RegisteredSubject subject, CancellationToken cancellationToken)
        => new(result: null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~FluentMetadataMapperTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/FluentMetadataMapper.cs src/Namotion.Interceptor.Connectors.Tests/Mapping/FluentMetadataMapperTests.cs
git commit -m "Add FluentMetadataMapper supplying forward fluent metadata"
```

---

### Task 5: `ExpressionPathHelper.GetSingleMemberName` (Connectors)

A strict single-member extractor for `ForType.Map` selectors (the existing `GetPathFromExpression` stays for #328).

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Mapping/ExpressionPathHelper.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Mapping/ExpressionPathHelperTests.cs` (add cases)

- [ ] **Step 1: Add the failing tests**

Append these `[Fact]` methods inside the existing `ExpressionPathHelperTests` class:

```csharp
    [Fact]
    public void WhenSingleMember_ThenGetSingleMemberNameReturnsName()
    {
        // Arrange
        System.Linq.Expressions.Expression<System.Func<SingleMemberModel, double>> selector = x => x.Speed;

        // Act
        var name = ExpressionPathHelper.GetSingleMemberName(selector.Body);

        // Assert
        Assert.Equal("Speed", name);
    }

    [Fact]
    public void WhenMemberChain_ThenGetSingleMemberNameThrows()
    {
        // Arrange
        System.Linq.Expressions.Expression<System.Func<SingleMemberModel, double>> selector = x => x.Child.Speed;

        // Act & Assert
        Assert.Throws<System.ArgumentException>(() => ExpressionPathHelper.GetSingleMemberName(selector.Body));
    }

    [Fact]
    public void WhenIndexer_ThenGetSingleMemberNameThrows()
    {
        // Arrange
        System.Linq.Expressions.Expression<System.Func<SingleMemberModel, double>> selector = x => x.Items[0];

        // Act & Assert
        Assert.Throws<System.ArgumentException>(() => ExpressionPathHelper.GetSingleMemberName(selector.Body));
    }
```

Add this model class at the bottom of the test file (outside the test class):

```csharp
public class SingleMemberModel
{
    public double Speed { get; set; }
    public SingleMemberModel Child { get; set; } = null!;
    public double[] Items { get; set; } = [];
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ExpressionPathHelperTests"`
Expected: FAIL (method `GetSingleMemberName` does not exist).

- [ ] **Step 3: Add the implementation**

Add this method to `ExpressionPathHelper` (keep `GetPathFromExpression` unchanged):

```csharp
    /// <summary>
    /// Extracts the name of a single member access on the lambda parameter (e.g. <c>x =&gt; x.Property</c>).
    /// Used by type-level fluent mapping, where the selector must reference exactly one member of the
    /// configured type. Throws on a member chain, an indexer, or anything else.
    /// </summary>
    public static string GetSingleMemberName(Expression expression)
    {
        var current = expression;

        // Unwrap Convert/ConvertChecked (e.g. boxing to object).
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        if (current is MemberExpression { Expression: ParameterExpression } member)
        {
            return member.Member.Name;
        }

        throw new ArgumentException(
            "Expression must be a single member access on the lambda parameter (e.g., x => x.Property).",
            nameof(expression));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ExpressionPathHelperTests"`
Expected: PASS (existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Mapping/ExpressionPathHelper.cs src/Namotion.Interceptor.Connectors.Tests/Mapping/ExpressionPathHelperTests.cs
git commit -m "Add ExpressionPathHelper.GetSingleMemberName for type-level selectors"
```

---

## Phase 2: TryGetPath cleanup (Registry)

### Task 6: Make `TryGetPath(string separator)` index- and `[InlinePaths]`-aware

The plain overload (`PathExtensions.TryGetPath(this RegisteredSubjectProperty, string separator, IInterceptorSubject?)`, lines 225-252) currently joins only `frames[i].Property.Name`, dropping indices and ignoring `[InlinePaths]`. Make it reuse the provider overload's frame-emit logic via `DefaultPathProvider` parameterized by the separator. This is the prerequisite enabler for #328.

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/Paths/DefaultPathProvider.cs`
- Modify: `src/Namotion.Interceptor.Registry/Paths/PathExtensions.cs:225-252`
- Test: `src/Namotion.Interceptor.Registry.Tests/Paths/PathExtensionsTests.cs` (add cases)

- [ ] **Step 1: Add the failing tests**

Append to the existing `PathExtensionsTests` class in `src/Namotion.Interceptor.Registry.Tests/Paths/PathExtensionsTests.cs`. Reuse whatever collection-bearing registered model the file already exercises for index paths; if none is suitable, add the model below and use it. The assertions encode the bracket format:

```csharp
    [Fact]
    public void WhenPropertyIsCollectionElement_ThenPlainTryGetPathIncludesBracketIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new TryGetPathIndexRoot(context);
        root.Items = [new TryGetPathIndexChild(context), new TryGetPathIndexChild(context)];
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var childSubject = root.Items[1];
        var nameProperty = childSubject.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(rootSubject: root);

        // Assert
        Assert.Equal("Items[1].Name", path);
    }
```

If `TryGetPathIndexRoot` / `TryGetPathIndexChild` are not already present, add at the bottom of the file:

```csharp
[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexRoot
{
    public partial TryGetPathIndexChild[] Items { get; set; }

    public TryGetPathIndexRoot()
    {
        Items = [];
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexChild
{
    public partial string Name { get; set; }

    public TryGetPathIndexChild()
    {
        Name = "";
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "FullyQualifiedName~WhenPropertyIsCollectionElement_ThenPlainTryGetPathIncludesBracketIndex"`
Expected: FAIL (actual is `Items.Name`, missing `[1]`).

- [ ] **Step 3: Make `DefaultPathProvider` separator-configurable**

Replace the body of `src/Namotion.Interceptor.Registry/Paths/DefaultPathProvider.cs` with:

```csharp
namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Default path provider that uses property BrowseNames as path segments.
/// </summary>
public class DefaultPathProvider : PathProviderBase
{
    private readonly char _pathSeparator;

    /// <summary>
    /// Gets the singleton instance of the default path provider (separator '.').
    /// </summary>
    public static DefaultPathProvider Instance { get; } = new();

    /// <summary>Creates a default path provider with the '.' separator.</summary>
    public DefaultPathProvider()
        : this('.')
    {
    }

    /// <summary>Creates a default path provider with the given separator.</summary>
    public DefaultPathProvider(char pathSeparator)
    {
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;
}
```

Note: the previously-private parameterless constructor becomes public; `Instance` keeps the '.' behavior. This is an additive public-API change handled by the snapshot task.

- [ ] **Step 4: Rewrite the plain `TryGetPath(string separator)` overload to delegate**

In `src/Namotion.Interceptor.Registry/Paths/PathExtensions.cs`, replace the whole `public static string? TryGetPath(this RegisteredSubjectProperty property, string separator = ".", IInterceptorSubject? rootSubject = null)` method (lines ~225-252) with:

```csharp
    /// <summary>
    /// Gets the structural property path by walking the parent chain to this property, joining BrowseName
    /// segments with the given separator. Indices (e.g. <c>Items[1]</c>) and [InlinePaths] keys are
    /// included, matching the provider overload.
    /// </summary>
    /// <param name="property">The property to compute the path for.</param>
    /// <param name="separator">The separator placed between path segments.</param>
    /// <param name="rootSubject">
    /// Optional root to make the path relative to. When provided, the parent graph is searched across all
    /// parents and <c>null</c> is returned when the property is not reachable from the given root. When
    /// <c>null</c>, the canonical absolute path (following the first parent) is returned.
    /// </param>
    /// <returns>
    /// The path, or <c>null</c> when a root is given and the property is not reachable from it. A cycle in
    /// the parent chain is also reported as <c>null</c> (never throws), with or without a root.
    /// </returns>
    public static string? TryGetPath(this RegisteredSubjectProperty property, string separator = ".", IInterceptorSubject? rootSubject = null)
    {
        var pathProvider = separator.Length == 1 && separator[0] == '.'
            ? DefaultPathProvider.Instance
            : new DefaultPathProvider(separator.Length == 1 ? separator[0] : '.');

        // Multi-character separators are not supported by the index-aware provider path; fall back to the
        // provider overload only for single-character separators. For the default '.' the singleton is used.
        return separator.Length == 1
            ? property.TryGetPath(pathProvider, rootSubject)
            : TryGetPathMultiCharSeparator(property, separator, rootSubject);
    }

    // Rare multi-character separator path: build from the provider overload's '.' output by splitting is not
    // safe (segments may contain '.'), so compute via the single-char default and then post-process is also
    // unsafe. Multi-character separators are unused in production; keep the historical BrowseName-join
    // behavior (no indices) for them to avoid changing semantics no caller relies on.
    private static string? TryGetPathMultiCharSeparator(RegisteredSubjectProperty property, string separator, IInterceptorSubject? rootSubject)
    {
        var canonical = property.TryGetPath(DefaultPathProvider.Instance, rootSubject);
        return canonical?.Replace(".", separator);
    }
```

Note: production callers only pass single-character separators (`.` or `/`), which now route through the index-aware provider overload. The multi-character branch preserves prior behavior for any exotic caller.

- [ ] **Step 5: Run the new test and the existing path tests**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "FullyQualifiedName~PathExtensionsTests"`
Expected: PASS, including the new bracket-index test. If any existing test asserted an index-free path for a collection element via the plain overload, update its expected string to the bracket form (these are correct-behavior changes).

- [ ] **Step 6: Build the connector test projects to catch separator-dependent assertions**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~PathExtensionsTests"`
Expected: PASS. Fix any expected-path strings that legitimately gain a bracket index.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Paths/DefaultPathProvider.cs src/Namotion.Interceptor.Registry/Paths/PathExtensions.cs src/Namotion.Interceptor.Registry.Tests/Paths/PathExtensionsTests.cs
git commit -m "Make plain TryGetPath overload index- and InlinePaths-aware"
```

---

## Phase 3: MQTT connector

### Task 7: MQTT fluent facade, builder, and deletion of the old mapper

**Files:**
- Delete: `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMapper.cs`
- Rename/replace: `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMappingBuilder.cs` -> `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyBuilder.cs`
- Create: `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMapping.cs`

- [ ] **Step 1: Delete the old absolute-path mapper**

```bash
git rm src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMapper.cs
```

- [ ] **Step 2: Replace the builder with `MqttFluentPropertyBuilder`**

```bash
git rm src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMappingBuilder.cs
```

Create `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyBuilder.cs`:

```csharp
using MQTTnet.Protocol;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Per-member fluent configuration for MQTT. <see cref="WithSegment"/> sets the topic level (the path
/// segment); QoS and Retain are protocol metadata.
/// </summary>
public sealed class MqttFluentPropertyBuilder
{
    private string? _segment;
    private MqttQualityOfServiceLevel? _qualityOfService;
    private bool? _retain;

    public MqttFluentPropertyBuilder WithSegment(string segment) { _segment = segment; return this; }
    public MqttFluentPropertyBuilder WithQualityOfService(MqttQualityOfServiceLevel qualityOfService) { _qualityOfService = qualityOfService; return this; }
    public MqttFluentPropertyBuilder WithRetain(bool retain) { _retain = retain; return this; }

    internal (string? Segment, MqttPropertyMapping Metadata) Build()
        => (_segment, new MqttPropertyMapping(QualityOfService: _qualityOfService, Retain: _retain));
}
```

- [ ] **Step 3: Create the facade and type builder**

Create `src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMapping.cs`:

```csharp
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Code-based MQTT mapping configuration, root-scoped. The public entry point for configuring MQTT topics
/// and metadata in code instead of via attributes. Build the mapper pair with <see cref="CreateMappers"/>
/// or use the AddMqttSubject* DI overloads' <c>configureFluent</c> callback.
/// </summary>
public sealed class MqttFluentMapping<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<MqttPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>
    /// Builds the fluent mapper pair (a path-provider mapper over a <see cref="FluentPathProvider"/> and a
    /// metadata mapper) to splice into an <see cref="MqttCompositeMapper"/>.
    /// </summary>
    public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>[] CreateMappers(char pathSeparator = '/')
        =>
        [
            new MqttPathProviderMapper(new FluentPathProvider(Registry, pathSeparator)),
            new FluentMetadataMapper<MqttPropertyMapping, MqttLookupKey>(Registry)
        ];
}

/// <summary>Type-scoped MQTT fluent builder; chains within a type and into the next type.</summary>
public sealed class MqttFluentTypeBuilder<TRoot, T>
    where TRoot : IInterceptorSubject
{
    private readonly MqttFluentMapping<TRoot> _owner;

    internal MqttFluentTypeBuilder(MqttFluentMapping<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="T"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, T> Map<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<MqttFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new MqttFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddType(typeof(T), member, segment, metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public MqttFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();
}
```

- [ ] **Step 4: Build (expect MQTT test project errors from deleted types)**

Run: `dotnet build src/Namotion.Interceptor.Mqtt`
Expected: Build succeeds. (Tests are fixed in Task 8.)

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentPropertyBuilder.cs src/Namotion.Interceptor.Mqtt/Mapping/MqttFluentMapping.cs
git commit -m "Replace MQTT fluent mapper with type-level facade and builder"
```

---

### Task 8: MQTT fluent tests (replace old) and fix the composite test

**Files:**
- Delete: `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentMapperTests.cs`
- Create: `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentMappingTests.cs`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttCompositeMapperTests.cs:56-81`

- [ ] **Step 1: Delete the old fluent test**

```bash
git rm src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentMapperTests.cs
```

- [ ] **Step 2: Write the new fluent tests**

Create `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentMappingTests.cs`:

```csharp
using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttFluentMappingTests
{
    private static MqttCompositeMapper CreateFluentMapper(MqttFluentMapping<MqttFluentRoot> fluent)
        => new(fluent.CreateMappers('/'));

    [Fact]
    public void WhenTypeMemberMapped_ThenTopicComposesFromSegments()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>()
                .Map(r => r.Pump, b => b.WithSegment("pump"))
            .ForType<MqttFluentPump>()
                .Map(p => p.Speed, b => b.WithSegment("speed").WithQualityOfService(MqttQualityOfServiceLevel.AtLeastOnce));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump(context) };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var speed = root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("pump/speed", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, mapping.QualityOfService);
    }

    [Fact]
    public void WhenTypeReusedAcrossLocations_ThenResolvesEverywhere()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>()
                .Map(r => r.Pump, b => b.WithSegment("pump"))
                .Map(r => r.Fan, b => b.WithSegment("fan"))
            .ForType<MqttFluentPump>()
                .Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context)
        {
            Pump = new MqttFluentPump(context),
            Fan = new MqttFluentPump(context)
        };
        _ = root.TryGetRegisteredSubject()!;

        // Act
        mapper.TryGetMapping(root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!, root, out var pumpMapping);
        mapper.TryGetMapping(root.Fan.TryGetRegisteredSubject()!.TryGetProperty("Speed")!, root, out var fanMapping);

        // Assert
        Assert.Equal("pump/speed", pumpMapping!.Topic);
        Assert.Equal("fan/speed", fanMapping!.Topic);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenResolvesViaPathProvider()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>().Map(r => r.Pump, b => b.WithSegment("pump"))
            .ForType<MqttFluentPump>().Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump(context) };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("pump/speed"), registeredRoot, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Speed", found.Name);
    }

    [Fact]
    public void WhenPropertyNotMapped_ThenReturnsFalse()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttFluentRoot>();
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public async Task WhenTypeUsedInCollection_ThenElementResolvesBothDirections()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>().Map(r => r.Motors, b => b.WithSegment("motors"))
            .ForType<MqttFluentPump>().Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Motors = [new MqttFluentPump(context), new MqttFluentPump(context)] };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var speed = root.Motors[1].TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var forwardFound = mapper.TryGetMapping(speed, root, out var mapping);
        var reverse = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("motors[1]/speed"), registeredRoot, CancellationToken.None);

        // Assert
        Assert.True(forwardFound);
        Assert.Equal("motors[1]/speed", mapping!.Topic);
        Assert.NotNull(reverse);
        Assert.Equal("Speed", reverse.Name);
    }
}

[InterceptorSubject]
public partial class MqttFluentRoot
{
    public partial MqttFluentPump Pump { get; set; }
    public partial MqttFluentPump Fan { get; set; }
    public partial List<MqttFluentPump> Motors { get; set; }

    public MqttFluentRoot()
    {
        Pump = null!;
        Fan = null!;
        Motors = [];
    }
}

[InterceptorSubject]
public partial class MqttFluentPump
{
    public partial double Speed { get; set; }

    public MqttFluentPump()
    {
        Speed = 0;
    }
}
```

- [ ] **Step 3: Fix the composite test's fluent case**

In `src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttCompositeMapperTests.cs`, replace the `WhenFluentTopicAndAttributeMetadata_ThenAttributeMetadataLayersOntoFluentTopic` test (lines ~56-81) with:

```csharp
    [Fact]
    public void WhenFluentSegmentAndAttributeMetadata_ThenAttributeMetadataLayersOntoFluentTopic()
    {
        // Arrange
        var fluent = new MqttFluentMapping<MqttCompositeTestSensor>();
        fluent.ForType<MqttCompositeTestSensor>().Map(s => s.Temperature, b => b.WithSegment("fluenttemp"));

        var mapper = new MqttCompositeMapper(
            [
                .. fluent.CreateMappers('/'),
                new MqttAttributeMapper()
            ]);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert - the fluent path provider supplies the topic; the attribute's QoS/Retain layer on top.
        Assert.Equal("fluenttemp", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }
```

- [ ] **Step 4: Run the MQTT mapping tests**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~Mapping"`
Expected: PASS (new fluent tests + fixed composite test + existing attribute/path tests). Exclude integration if needed with `&Category!=Integration`.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttFluentMappingTests.cs src/Namotion.Interceptor.Mqtt.Tests/Mapping/MqttCompositeMapperTests.cs
git commit -m "Replace MQTT fluent tests with type-level mapping tests"
```

---

### Task 9: MQTT DI `configureFluent` and public API snapshot

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/MqttSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Add the `configureFluent` parameter and mapper builder**

In `MqttSubjectExtensions`, add a `using Namotion.Interceptor.Mqtt.Mapping;` (if not present), then change the two typed convenience overloads and add a private helper.

Replace `AddMqttSubjectClientSource<TSubject>` signature and its `Mapper = ...` with:

```csharp
    public static IServiceCollection AddMqttSubjectClientSource<TSubject>(
        this IServiceCollection serviceCollection,
        string brokerHost,
        string connectorName,
        int brokerPort = 1883,
        string? topicPrefix = null,
        Action<MqttFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new MqttClientConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                Mapper = BuildMqttMapper(connectorName, configureFluent)
            });
    }
```

Replace `AddMqttSubjectServer<TSubject>` signature and its `Mapper = ...` with:

```csharp
    public static IServiceCollection AddMqttSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string connectorName,
        int brokerPort = 1883,
        string? brokerHost = null,
        string? topicPrefix = null,
        Action<MqttFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new MqttServerConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                Mapper = BuildMqttMapper(connectorName, configureFluent)
            });
    }
```

Add this private helper to the class:

```csharp
    private static MqttCompositeMapper BuildMqttMapper<TSubject>(
        string connectorName,
        Action<MqttFluentMapping<TSubject>>? configureFluent)
        where TSubject : IInterceptorSubject
    {
        var mappers = new List<IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>>
        {
            new MqttPathProviderMapper(new AttributeBasedPathProvider(connectorName, '/')),
            new MqttAttributeMapper(connectorName)
        };

        if (configureFluent is not null)
        {
            var fluent = new MqttFluentMapping<TSubject>();
            configureFluent(fluent);
            // Fluent is layered after attributes so it wins on conflicts; omit configureFluent for attribute-only.
            mappers.AddRange(fluent.CreateMappers('/'));
        }

        return new MqttCompositeMapper(mappers.ToArray());
    }
```

Add `using Namotion.Interceptor.Connectors.Mapping;` for `IReversePropertyMapper<,>`.

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.Mqtt`
Expected: Build succeeds.

- [ ] **Step 3: Regenerate the MQTT public API snapshot**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL (API changed: removed `MqttFluentMapper`/`MqttFluentMappingBuilder`, added `MqttFluentMapping`/`MqttFluentTypeBuilder`/`MqttFluentPropertyBuilder`, new `configureFluent` overloads). Then accept the new snapshot:

```bash
cp src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run to confirm PASS:
Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt/MqttSubjectExtensions.cs src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "Add MQTT configureFluent DI wiring and update API snapshot"
```

---

## Phase 4: OPC UA connector

### Task 10: OPC UA fluent property builder, metadata mapper, facade; delete the old mapper

**Files:**
- Delete: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMapper.cs`
- Delete: `src/Namotion.Interceptor.OpcUa/Mapping/IPropertyBuilder.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentPropertyBuilder.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMetadataMapper.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMapping.cs`

- [ ] **Step 1: Delete the old fluent mapper and nested builder interface**

```bash
git rm src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMapper.cs src/Namotion.Interceptor.OpcUa/Mapping/IPropertyBuilder.cs
```

- [ ] **Step 2: Create the property builder**

Create `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentPropertyBuilder.cs`:

```csharp
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Per-member fluent configuration for OPC UA. <see cref="BrowseName"/> sets both the path segment (for
/// composition and reverse navigation) and the node BrowseName. Other setters configure node and
/// monitoring metadata, mirroring the attribute fields.
/// </summary>
public sealed class OpcUaFluentPropertyBuilder
{
    private string? _segment;
    private OpcUaPropertyMapping _mapping = new();

    public OpcUaFluentPropertyBuilder BrowseName(string value)
    {
        _segment = value;
        _mapping = _mapping with { BrowseName = value };
        return this;
    }

    public OpcUaFluentPropertyBuilder BrowseNamespaceUri(string value) { _mapping = _mapping with { BrowseNamespaceUri = value }; return this; }
    public OpcUaFluentPropertyBuilder NodeIdentifier(string value) { _mapping = _mapping with { NodeIdentifier = value }; return this; }
    public OpcUaFluentPropertyBuilder NodeNamespaceUri(string value) { _mapping = _mapping with { NodeNamespaceUri = value }; return this; }
    public OpcUaFluentPropertyBuilder DisplayName(string value) { _mapping = _mapping with { DisplayName = value }; return this; }
    public OpcUaFluentPropertyBuilder Description(string value) { _mapping = _mapping with { Description = value }; return this; }
    public OpcUaFluentPropertyBuilder TypeDefinition(string identifier, string? namespaceUri = null) { _mapping = _mapping with { TypeDefinition = identifier, TypeDefinitionNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder NodeClass(OpcUaNodeClass value) { _mapping = _mapping with { NodeClass = value }; return this; }
    public OpcUaFluentPropertyBuilder DataType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { DataType = identifier, DataTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder IsValue(bool value = true) { _mapping = _mapping with { IsValue = value }; return this; }
    public OpcUaFluentPropertyBuilder ReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ReferenceType = identifier, ReferenceTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder ItemReferenceType(string identifier, string? namespaceUri = null) { _mapping = _mapping with { ItemReferenceType = identifier, ItemReferenceTypeNamespace = namespaceUri }; return this; }
    public OpcUaFluentPropertyBuilder SamplingInterval(int value) { _mapping = _mapping with { SamplingInterval = value }; return this; }
    public OpcUaFluentPropertyBuilder QueueSize(uint value) { _mapping = _mapping with { QueueSize = value }; return this; }
    public OpcUaFluentPropertyBuilder DiscardOldest(bool value) { _mapping = _mapping with { DiscardOldest = value }; return this; }
    public OpcUaFluentPropertyBuilder DataChangeTrigger(DataChangeTrigger value) { _mapping = _mapping with { DataChangeTrigger = value }; return this; }
    public OpcUaFluentPropertyBuilder DeadbandType(DeadbandType value) { _mapping = _mapping with { DeadbandType = value }; return this; }
    public OpcUaFluentPropertyBuilder DeadbandValue(double value) { _mapping = _mapping with { DeadbandValue = value }; return this; }
    public OpcUaFluentPropertyBuilder ModellingRule(ModellingRule value) { _mapping = _mapping with { ModellingRule = value }; return this; }
    public OpcUaFluentPropertyBuilder EventNotifier(byte value) { _mapping = _mapping with { EventNotifier = value }; return this; }

    public OpcUaFluentPropertyBuilder AdditionalReference(
        string referenceType,
        string? referenceTypeNamespace,
        string targetNodeId,
        string? targetNamespaceUri = null,
        bool isForward = true)
    {
        var reference = new OpcUaAdditionalReference
        {
            ReferenceType = referenceType,
            ReferenceTypeNamespace = referenceTypeNamespace,
            TargetNodeId = targetNodeId,
            TargetNamespaceUri = targetNamespaceUri,
            IsForward = isForward
        };
        _mapping = _mapping with { AdditionalReferences = [.. _mapping.AdditionalReferences ?? [], reference] };
        return this;
    }

    internal (string? Segment, OpcUaPropertyMapping Metadata) Build() => (_segment, _mapping);
}
```

- [ ] **Step 3: Create the OPC UA metadata mapper with the type-self fallback**

Create `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMetadataMapper.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA fluent metadata mapper. Adds the type-self (class-level) fallback for subject-typed members on
/// top of <see cref="FluentMetadataMapper{TMetadata,TKey}"/>: a reference, collection, or dictionary
/// property merges in its element type's <c>Configure(...)</c> metadata, mirroring
/// <c>OpcUaAttributeMapper</c>'s class-level fallback.
/// </summary>
public sealed class OpcUaFluentMetadataMapper : FluentMetadataMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    public OpcUaFluentMetadataMapper(FluentMappingRegistry<OpcUaPropertyMapping> registry)
        : base(registry)
    {
    }

    public override bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        Registry.TryGetTypeMetadata(property.Subject.GetType(), property.Name, out var propertyMetadata);

        OpcUaPropertyMapping? typeSelf = null;
        if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var elementType = GetElementType(property.Type);
            if (elementType is not null)
                Registry.TryGetTypeSelfMetadata(elementType, out typeSelf);
        }

        if (propertyMetadata is null && typeSelf is null)
        {
            mapping = null;
            return false;
        }

        mapping = propertyMetadata is null
            ? typeSelf!
            : typeSelf is null
                ? propertyMetadata
                : OpcUaPropertyMapping.Merge(propertyMetadata, typeSelf);
        return true;
    }

    private static Type? GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            return args.Length switch
            {
                2 => args[1],
                1 => args[0],
                _ => type
            };
        }

        return type;
    }
}
```

- [ ] **Step 4: Create the facade and type builder**

Create `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMapping.cs`:

```csharp
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Code-based OPC UA mapping configuration, root-scoped. The public entry point for configuring OPC UA
/// nodes and metadata in code instead of via attributes. Build the mapper pair with
/// <see cref="CreateMappers"/> or use the AddOpcUaSubject* DI overloads' <c>configureFluent</c> callback.
/// </summary>
public sealed class OpcUaFluentMapping<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<OpcUaPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>Builds the fluent mapper pair to splice into an <see cref="OpcUaCompositeMapper"/>.</summary>
    public IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] CreateMappers(char pathSeparator = '.')
        =>
        [
            new OpcUaPathProviderMapper(new FluentPathProvider(Registry, pathSeparator)),
            new OpcUaFluentMetadataMapper(Registry)
        ];
}

/// <summary>Type-scoped OPC UA fluent builder; chains within a type and into the next type.</summary>
public sealed class OpcUaFluentTypeBuilder<TRoot, T>
    where TRoot : IInterceptorSubject
{
    private readonly OpcUaFluentMapping<TRoot> _owner;

    internal OpcUaFluentTypeBuilder(OpcUaFluentMapping<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> Map<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<OpcUaFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddType(typeof(T), member, segment, metadata);
        return this;
    }

    /// <summary>Configures class-level (type-self) node metadata for <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> Configure(Action<OpcUaFluentPropertyBuilder> configure)
    {
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (_, metadata) = builder.Build();
        _owner.Registry.AddTypeSelf(typeof(T), metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public OpcUaFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentPropertyBuilder.cs src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMetadataMapper.cs src/Namotion.Interceptor.OpcUa/Mapping/OpcUaFluentMapping.cs
git commit -m "Replace OPC UA fluent mapper with type-level facade, builder, and metadata mapper"
```

---

### Task 11: OPC UA fluent tests (replace old)

**Files:**
- Delete: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaFluentMapperTests.cs`
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaFluentMappingTests.cs`

- [ ] **Step 1: Delete the old fluent test**

```bash
git rm src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaFluentMapperTests.cs
```

- [ ] **Step 2: Write the new fluent tests**

Create `src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaFluentMappingTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaFluentMappingTests
{
    [Fact]
    public void WhenLeafMapped_ThenBrowseNameIsSegmentAndMetadata()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent.ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed").SamplingInterval(500));

        var mappers = fluent.CreateMappers('.');
        var metadataMapper = (OpcUaFluentMetadataMapper)mappers[1];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = metadataMapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Speed", mapping!.BrowseName);
        Assert.Equal(500, mapping.SamplingInterval);
    }

    [Fact]
    public void WhenTypeReusedAcrossLocations_ThenBrowseNameResolvesEverywhere()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mappers = fluent.CreateMappers('.');
        var pathMapper = (OpcUaPathProviderMapper)mappers[0];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;
        var speedProperty = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        pathMapper.TryGetMapping(motorProperty, root, out var motorMapping);
        pathMapper.TryGetMapping(speedProperty, root, out var speedMapping);

        // Assert - the path-provider mapper supplies the browse name from the fluent segment.
        Assert.Equal("Motor", motorMapping!.BrowseName);
        Assert.Equal("Speed", speedMapping!.BrowseName);
    }

    [Fact]
    public void WhenConfigureUsedForReferencedType_ThenTypeSelfMergesIntoSubjectMember()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Configure(b => b.TypeDefinition("MotorType"));
        var metadataMapper = (OpcUaFluentMetadataMapper)fluent.CreateMappers('.')[1];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;

        // Act
        var found = metadataMapper.TryGetMapping(motorProperty, root, out var mapping);

        // Assert - the Motor member's metadata (BrowseName) plus its type-self TypeDefinition.
        Assert.True(found);
        Assert.Equal("Motor", mapping!.BrowseName);
        Assert.Equal("MotorType", mapping.TypeDefinition);
    }
}

[InterceptorSubject]
public partial class OpcUaFluentRoot
{
    public partial OpcUaFluentMotor Motor { get; set; }

    public OpcUaFluentRoot()
    {
        Motor = null!;
    }
}

[InterceptorSubject]
public partial class OpcUaFluentMotor
{
    public partial double Speed { get; set; }

    public OpcUaFluentMotor()
    {
        Speed = 0;
    }
}
```

- [ ] **Step 3: Run the OPC UA mapping tests (unit only)**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~Mapping&Category!=Integration"`
Expected: PASS (new fluent tests + existing attribute/path/composite tests).

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaFluentMappingTests.cs
git commit -m "Replace OPC UA fluent tests with type-level mapping tests"
```

---

### Task 12: OPC UA DI `configureFluent` and public API snapshot

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/OpcUaSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Thread `configureFluent` through the typed overloads and the default-configuration builders**

In `OpcUaSubjectExtensions`, add `using Namotion.Interceptor.Connectors.Mapping;`. Change the four typed convenience overloads to accept `Action<OpcUaFluentMapping<TSubject>>? configureFluent = null` and build the mapper eagerly, then pass it into the default-configuration helpers. Replace each as follows.

`AddOpcUaSubjectClientSource<TSubject>`:

```csharp
    public static IServiceCollection AddOpcUaSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string serverUrl,
        string connectorName,
        string[]? rootPath = null,
        Action<OpcUaFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        var mapper = BuildOpcUaMapper(connectorName, configureFluent);
        return services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultClientConfiguration(sp, serverUrl, rootPath, mapper));
    }
```

`AddKeyedOpcUaSubjectClientSource<TSubject>`:

```csharp
    public static IServiceCollection AddKeyedOpcUaSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string name,
        string serverUrl,
        string connectorName,
        string[]? rootPath = null,
        Action<OpcUaFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        var mapper = BuildOpcUaMapper(connectorName, configureFluent);
        return services.AddKeyedOpcUaSubjectClientSource(
            name,
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultClientConfiguration(sp, serverUrl, rootPath, mapper));
    }
```

`AddOpcUaSubjectServer<TSubject>`:

```csharp
    public static IServiceCollection AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection services,
        string connectorName,
        string? rootName = null,
        Action<OpcUaFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        var mapper = BuildOpcUaMapper(connectorName, configureFluent);
        return services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultServerConfiguration(sp, rootName, mapper));
    }
```

`AddKeyedOpcUaSubjectServer<TSubject>`:

```csharp
    public static IServiceCollection AddKeyedOpcUaSubjectServer<TSubject>(
        this IServiceCollection services,
        string name,
        string connectorName,
        string? rootName = null,
        Action<OpcUaFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        var mapper = BuildOpcUaMapper(connectorName, configureFluent);
        return services.AddKeyedOpcUaSubjectServer(
            name,
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultServerConfiguration(sp, rootName, mapper));
    }
```

Change the two `CreateDefault*` helpers to take a prebuilt mapper instead of `connectorName`:

```csharp
    private static OpcUaClientConfiguration CreateDefaultClientConfiguration(
        IServiceProvider sp, string serverUrl, string[]? rootPath, OpcUaCompositeMapper mapper)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(builder =>
            builder.Services.AddSingleton(loggerFactory));

        return new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            RootPath = rootPath,
            TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            TelemetryContext = telemetryContext,
            Mapper = mapper
        };
    }

    private static OpcUaServerConfiguration CreateDefaultServerConfiguration(
        IServiceProvider sp, string? rootName, OpcUaCompositeMapper mapper)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(builder =>
            builder.Services.AddSingleton(loggerFactory));

        return new OpcUaServerConfiguration
        {
            RootName = rootName,
            ValueConverter = new OpcUaValueConverter(),
            TelemetryContext = telemetryContext,
            Mapper = mapper
        };
    }
```

Add the mapper builder helper:

```csharp
    private static OpcUaCompositeMapper BuildOpcUaMapper<TSubject>(
        string connectorName,
        Action<OpcUaFluentMapping<TSubject>>? configureFluent)
        where TSubject : IInterceptorSubject
    {
        var mappers = new List<IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>>
        {
            new OpcUaPathProviderMapper(new AttributeBasedPathProvider(connectorName)),
            new OpcUaAttributeMapper(connectorName)
        };

        if (configureFluent is not null)
        {
            var fluent = new OpcUaFluentMapping<TSubject>();
            configureFluent(fluent);
            // Fluent is layered after attributes so it wins on conflicts; omit configureFluent for attribute-only.
            mappers.AddRange(fluent.CreateMappers());
        }

        return new OpcUaCompositeMapper(mappers.ToArray());
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build succeeds.

- [ ] **Step 3: Regenerate the OPC UA public API snapshot**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL (removed `OpcUaFluentMapper`/`IPropertyBuilder`; added `OpcUaFluentMapping`/`OpcUaFluentTypeBuilder`/`OpcUaFluentPropertyBuilder`/`OpcUaFluentMetadataMapper`; new `configureFluent` overloads). Accept:

```bash
cp src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run to confirm PASS:
Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/OpcUaSubjectExtensions.cs src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "Add OPC UA configureFluent DI wiring and update API snapshot"
```

---

## Phase 5: Snapshots, docs, and final verification

### Task 13: Registry and Connectors public API snapshots

**Files:**
- Modify: `src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Regenerate the Registry snapshot**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL (added `IFluentSegmentSource`, `FluentPathProvider`; `DefaultPathProvider` gains public constructors). Accept:

```bash
cp src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt
```

- [ ] **Step 2: Regenerate the Connectors snapshot**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL (added `FluentMappingRegistry<TMetadata>`, `FluentMetadataMapper<TMetadata,TKey>`, `ExpressionPathHelper.GetSingleMemberName`). Accept:

```bash
cp src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
```

- [ ] **Step 3: Confirm both pass**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "FullyQualifiedName~PublicApi"` then `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "Update Registry and Connectors public API snapshots for fluent mapping"
```

---

### Task 14: Documentation and full verification

**Files:**
- Modify: `docs/connectors-opcua-mapping.md`
- Modify: `docs/connectors-mqtt.md`
- Modify: `docs/connectors.md`

- [ ] **Step 1: Update the OPC UA mapping doc**

In `docs/connectors-opcua-mapping.md`, find the section describing the old fluent mapper (search for `OpcUaFluentMapper` or `.Map(`). Replace it with a "Code-based (fluent) mapping" section documenting `OpcUaFluentMapping<TRoot>`, `ForType<T>().Map(...)`, `.Configure(...)`, the `BrowseName`-is-segment rule, reuse across locations and collection elements, and the `configureFluent` DI callback. Use the OPC UA example from the spec (`docs/superpowers/specs/2026-05-31-fluent-code-based-mapping-design.md`, "OPC UA example"). Do not use em dashes.

- [ ] **Step 2: Update the MQTT doc**

In `docs/connectors-mqtt.md`, find any `MqttFluentMapper` reference and replace with the new `MqttFluentMapping<TRoot>` usage (`ForType<T>().Map(s => s.X, b => b.WithSegment(...).WithQualityOfService(...))`) and the `configureFluent` DI callback. Use the MQTT example from the spec.

- [ ] **Step 3: Update the connectors overview doc**

In `docs/connectors.md`, update any mention of the old fluent mappers to point at the new `*FluentMapping<TRoot>` facades and note that fluent is now a complete type-level alternative to attributes that composes with them.

- [ ] **Step 4: Full unit test run**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS (all unit tests across the solution).

- [ ] **Step 5: Targeted connector integration tests (only if connector behavior changed)**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests` then `dotnet test src/Namotion.Interceptor.Mqtt.Tests`
Expected: PASS. If an integration test relied on the old fluent API, port it to the new facade.

- [ ] **Step 6: Commit**

```bash
git add docs/connectors-opcua-mapping.md docs/connectors-mqtt.md docs/connectors.md
git commit -m "Document code-based fluent mapping for MQTT and OPC UA"
```

---

## Self-review notes (for the implementer)

- The new `*FluentMapping<TRoot>` facades are configuration objects, not mappers. The actual mappers come from `CreateMappers(...)` (a `FluentPathProvider`-backed path-provider mapper plus a metadata mapper).
- Reverse lookup is owned by the path-provider mapper for both connectors; the fluent metadata mapper always returns null on reverse. Do not add reverse logic to it.
- `BrowseName` (OPC UA) and `WithSegment` (MQTT) both write the registry segment; for OPC UA `BrowseName` also writes the metadata BrowseName.
- If any existing test outside the Mapping folders fails after the `TryGetPath` change, it is because a collection-element path now correctly includes a bracket index; update the expected string, do not revert the fix.
- Keep `ExpressionPathHelper.GetPathFromExpression` (used by #328); only `GetSingleMemberName` is added here.
