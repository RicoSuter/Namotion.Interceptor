# Runtime Property Metadata Cache Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the source-generated `DefaultProperties` dictionary with a runtime cache that builds property metadata on first access, reducing generated code from ~10 lines per property to ~1 line.

**Architecture:** The generator emits minimal `__GetPropertyAccessors()` switch expression returning delegate tuples. A new `SubjectPropertyMetadataCache` class uses reflection to discover properties, walk inheritance chains, and build `FrozenDictionary<string, SubjectPropertyMetadata>` on first access per type.

**Tech Stack:** C# 13 partial properties, System.Reflection, FrozenDictionary (.NET 8+), ConcurrentDictionary, Source Generators

---

## Phase 1: Add Runtime Cache Infrastructure

### Task 1.1: Multi-target Core Library

**Files:**
- Modify: `src/Namotion.Interceptor/Namotion.Interceptor.csproj`

**Step 1: Read current project file**

```bash
cat src/Namotion.Interceptor/Namotion.Interceptor.csproj
```

**Step 2: Update target frameworks**

Change:
```xml
<TargetFramework>netstandard2.0</TargetFramework>
```

To:
```xml
<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor/Namotion.Interceptor.csproj`
Expected: Build succeeds for both targets

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor/Namotion.Interceptor.csproj
git commit -m "chore: multi-target core library for netstandard2.0 and net8.0

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.2: Create InterceptedAttribute

**Files:**
- Create: `src/Namotion.Interceptor/Attributes/InterceptedAttribute.cs`
- Reference: `src/Namotion.Interceptor/Attributes/DerivedAttribute.cs`

**Step 1: Write failing test**

Create: `src/Namotion.Interceptor.Tests/Attributes/InterceptedAttributeTests.cs`

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests.Attributes;

public class InterceptedAttributeTests
{
    [Fact]
    public void InterceptedAttribute_CanBeAppliedToProperty()
    {
        // Arrange
        var attribute = new InterceptedAttribute();

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void InterceptedAttribute_HasCorrectAttributeUsage()
    {
        // Arrange
        var attributeUsage = typeof(InterceptedAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        // Assert
        Assert.Equal(AttributeTargets.Property, attributeUsage.ValidOn);
        Assert.False(attributeUsage.AllowMultiple);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~InterceptedAttributeTests" -v n`
Expected: FAIL with "type or namespace 'InterceptedAttribute' could not be found"

**Step 3: Create InterceptedAttribute**

Create: `src/Namotion.Interceptor/Attributes/InterceptedAttribute.cs`

```csharp
namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Marks a property as intercepted by the source generator.
/// Applied automatically to generated partial property implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class InterceptedAttribute : Attribute
{
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~InterceptedAttributeTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Attributes/InterceptedAttribute.cs src/Namotion.Interceptor.Tests/Attributes/InterceptedAttributeTests.cs
git commit -m "feat: add InterceptedAttribute for marking generated properties

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.3: Create SubjectPropertyMetadataCache - Basic Structure

**Files:**
- Create: `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`

**Step 1: Write failing test for cache existence**

Create: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

```csharp
using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tests;

public class SubjectPropertyMetadataCacheTests
{
    [Fact]
    public void Get_ReturnsEmptyDictionary_ForTypeWithNoProperties()
    {
        // Act
        var result = SubjectPropertyMetadataCache.Get<EmptySubject>();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [InterceptorSubject]
    private partial class EmptySubject
    {
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_ReturnsEmptyDictionary" -v n`
Expected: FAIL with "type or namespace 'SubjectPropertyMetadataCache' could not be found"

**Step 3: Create basic cache structure**

Create: `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`

```csharp
using System.Collections.Concurrent;
using System.Reflection;

#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif

namespace Namotion.Interceptor;

/// <summary>
/// Provides cached property metadata for interceptor subject types.
/// Metadata is built once per type using reflection on first access.
/// </summary>
public static class SubjectPropertyMetadataCache
{
#if NET8_0_OR_GREATER
    private static readonly ConcurrentDictionary<Type, FrozenDictionary<string, SubjectPropertyMetadata>> Cache = new();
#else
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, SubjectPropertyMetadata>> Cache = new();
#endif

    /// <summary>
    /// Gets the cached property metadata dictionary for the specified type.
    /// </summary>
    /// <typeparam name="T">The interceptor subject type.</typeparam>
    /// <returns>A read-only dictionary mapping property names to their metadata.</returns>
    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> Get<T>()
        where T : IInterceptorSubject
    {
        return Cache.GetOrAdd(typeof(T), static type => BuildMetadata(type));
    }

#if NET8_0_OR_GREATER
    private static FrozenDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#else
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#endif
    {
        var result = new Dictionary<string, SubjectPropertyMetadata>();

#if NET8_0_OR_GREATER
        return result.ToFrozenDictionary();
#else
        return result;
#endif
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_ReturnsEmptyDictionary" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "feat: add SubjectPropertyMetadataCache basic structure

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.4: Implement Inheritance Chain Walking

**Files:**
- Modify: `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`
- Modify: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

**Step 1: Write failing test for property discovery**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_DiscoversPropertiesFromType()
{
    // Act
    var result = SubjectPropertyMetadataCache.Get<SimpleSubject>();

    // Assert
    Assert.Contains("Name", result.Keys);
    Assert.Contains("Value", result.Keys);
}

[InterceptorSubject]
private partial class SimpleSubject
{
    public partial string Name { get; set; }
    public partial int Value { get; set; }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_DiscoversPropertiesFromType" -v n`
Expected: FAIL (empty dictionary)

**Step 3: Implement inheritance walking**

Update `SubjectPropertyMetadataCache.cs` `BuildMetadata` method:

```csharp
#if NET8_0_OR_GREATER
    private static FrozenDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#else
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#endif
    {
        var result = new Dictionary<string, SubjectPropertyMetadata>();

        // Walk inheritance chain (most derived first)
        var currentType = type;
        while (currentType != null && typeof(IInterceptorSubject).IsAssignableFrom(currentType))
        {
            foreach (var property in GetDeclaredProperties(currentType))
            {
                if (result.ContainsKey(property.Name))
                    continue; // Most derived wins

                var (getter, setter) = GetGeneratedAccessors(currentType, property.Name);
                if (getter == null && setter == null)
                {
                    (getter, setter) = CreateFallbackAccessors(property);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getter,
                    setter,
                    isIntercepted: property.GetCustomAttribute<Attributes.InterceptedAttribute>() != null,
                    isDynamic: false);
            }

            currentType = currentType.BaseType;
        }

#if NET8_0_OR_GREATER
        return result.ToFrozenDictionary();
#else
        return result;
#endif
    }

    private static IEnumerable<PropertyInfo> GetDeclaredProperties(Type type)
    {
        return type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        GetGeneratedAccessors(Type type, string propertyName)
    {
        var method = type.GetMethod(
            "__GetPropertyAccessors",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method == null)
            return (null, null);

        return ((Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?))
            method.Invoke(null, new object[] { propertyName })!;
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        CreateFallbackAccessors(PropertyInfo property)
    {
        return (CreateFallbackGetter(property), CreateFallbackSetter(property));
    }

    private static Func<IInterceptorSubject, object?>? CreateFallbackGetter(PropertyInfo property)
    {
        var getMethod = property.GetGetMethod(true);
        if (getMethod == null) return null;

        // Reflection-based fallback (works on all platforms including AOT)
        return subject => property.GetValue(subject);
    }

    private static Action<IInterceptorSubject, object?>? CreateFallbackSetter(PropertyInfo property)
    {
        var setMethod = property.GetSetMethod(true);
        if (setMethod == null) return null;

        // Reflection-based fallback (works on all platforms including AOT)
        return (subject, value) => property.SetValue(subject, value);
    }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_DiscoversPropertiesFromType" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "feat: implement inheritance chain walking in cache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.5: Implement Base Class Property Inheritance

**Files:**
- Modify: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

**Step 1: Write failing test for base class properties**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_DiscoversPropertiesFromBaseClass()
{
    // Act
    var result = SubjectPropertyMetadataCache.Get<DerivedSubject>();

    // Assert
    Assert.Contains("BaseName", result.Keys);  // From base
    Assert.Contains("DerivedValue", result.Keys);  // From derived
}

[InterceptorSubject]
private partial class BaseSubject
{
    public partial string BaseName { get; set; }
}

[InterceptorSubject]
private partial class DerivedSubject : BaseSubject
{
    public partial int DerivedValue { get; set; }
}
```

**Step 2: Run test to verify it passes (should work with current implementation)**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_DiscoversPropertiesFromBaseClass" -v n`
Expected: PASS (inheritance walking already implemented)

**Step 3: Write test for property override**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_MostDerivedPropertyWins_ForOverrides()
{
    // Act
    var result = SubjectPropertyMetadataCache.Get<OverridingSubject>();

    // Assert - should have only one "Name" property from most derived
    var nameCount = result.Keys.Count(k => k == "Name");
    Assert.Equal(1, nameCount);
}

[InterceptorSubject]
private partial class SubjectWithVirtualProperty
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
private partial class OverridingSubject : SubjectWithVirtualProperty
{
    public override partial string Name { get; set; }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_MostDerivedPropertyWins" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "test: add base class property inheritance tests for cache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.6: Implement Interface Default Implementation Detection

**Files:**
- Modify: `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`
- Modify: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

**Step 1: Write failing test for interface default implementations**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_DiscoversInterfaceDefaultImplementations()
{
    // Act
    var result = SubjectPropertyMetadataCache.Get<SubjectWithInterfaceDefault>();

    // Assert
    Assert.Contains("Value", result.Keys);  // Class property
    Assert.Contains("ComputedValue", result.Keys);  // Interface default
}

public interface IHasComputedValue
{
    double Value { get; }
    double ComputedValue => Value * 2;
}

[InterceptorSubject]
private partial class SubjectWithInterfaceDefault : IHasComputedValue
{
    public partial double Value { get; set; }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_DiscoversInterfaceDefaultImplementations" -v n`
Expected: FAIL (interface defaults not discovered yet)

**Step 3: Implement interface default detection**

Add to `SubjectPropertyMetadataCache.cs`:

```csharp
#if NET8_0_OR_GREATER
    private static FrozenDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#else
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#endif
    {
        var result = new Dictionary<string, SubjectPropertyMetadata>();

        // Walk inheritance chain (most derived first)
        var currentType = type;
        while (currentType != null && typeof(IInterceptorSubject).IsAssignableFrom(currentType))
        {
            foreach (var property in GetDeclaredProperties(currentType))
            {
                if (result.ContainsKey(property.Name))
                    continue;

                var (getter, setter) = GetGeneratedAccessors(currentType, property.Name);
                if (getter == null && setter == null)
                {
                    (getter, setter) = CreateFallbackAccessors(property);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getter,
                    setter,
                    isIntercepted: property.GetCustomAttribute<Attributes.InterceptedAttribute>() != null,
                    isDynamic: false);
            }

            currentType = currentType.BaseType;
        }

        // Add interface default implementations
        AddInterfaceDefaultImplementations(type, result);

#if NET8_0_OR_GREATER
        return result.ToFrozenDictionary();
#else
        return result;
#endif
    }

    private static IEnumerable<Type> GetInterfacesSortedByDepth(Type type)
    {
        var interfaces = type.GetInterfaces();
        var interfaceSet = new HashSet<Type>(interfaces);

        // Most derived (highest depth) first - ensures shadowing works correctly
        return interfaces.OrderByDescending(iface =>
            iface.GetInterfaces().Count(i => interfaceSet.Contains(i)));
    }

    private static void AddInterfaceDefaultImplementations(
        Type type,
        Dictionary<string, SubjectPropertyMetadata> result)
    {
        foreach (var iface in GetInterfacesSortedByDepth(type))
        {
            var map = type.GetInterfaceMap(iface);

            foreach (var property in iface.GetProperties())
            {
                if (result.ContainsKey(property.Name))
                    continue;

                var getter = property.GetGetMethod();
                if (getter == null || getter.IsAbstract)
                    continue;

                var index = Array.IndexOf(map.InterfaceMethods, getter);
                if (index < 0 || map.TargetMethods[index].DeclaringType != iface)
                    continue;

                var (getterDelegate, setterDelegate) = GetGeneratedAccessors(type, property.Name);
                if (getterDelegate == null)
                {
                    (getterDelegate, setterDelegate) = CreateInterfaceFallbackAccessors(property, iface);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getterDelegate,
                    setterDelegate,
                    isIntercepted: false,
                    isDynamic: false);
            }
        }
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        CreateInterfaceFallbackAccessors(PropertyInfo property, Type interfaceType)
    {
        var getter = CreateInterfaceFallbackGetter(property, interfaceType);
        var setter = CreateInterfaceFallbackSetter(property, interfaceType);
        return (getter, setter);
    }

    private static Func<IInterceptorSubject, object?>? CreateInterfaceFallbackGetter(PropertyInfo property, Type interfaceType)
    {
        var getMethod = property.GetGetMethod(true);
        if (getMethod == null) return null;

        // For interface default implementations, we need to invoke through the interface
        return subject => property.GetValue(subject);
    }

    private static Action<IInterceptorSubject, object?>? CreateInterfaceFallbackSetter(PropertyInfo property, Type interfaceType)
    {
        var setMethod = property.GetSetMethod(true);
        if (setMethod == null) return null;

        return (subject, value) => property.SetValue(subject, value);
    }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_DiscoversInterfaceDefaultImplementations" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "feat: implement interface default implementation detection in cache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.7: Test Interface Property Shadowing

**Files:**
- Modify: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

**Step 1: Write test for interface shadowing**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_MostDerivedInterfaceWins_ForShadowedProperties()
{
    // Act
    var result = SubjectPropertyMetadataCache.Get<SubjectWithShadowedInterface>();

    // Assert - should have "Status" from the most derived interface
    Assert.Contains("Status", result.Keys);
    var statusMetadata = result["Status"];
    // The getter should return "Derived" not "Base"
}

public interface IBaseStatus
{
    string Status => "Base";
}

public interface IDerivedStatus : IBaseStatus
{
    new string Status => "Derived";
}

[InterceptorSubject]
private partial class SubjectWithShadowedInterface : IDerivedStatus
{
    public partial int Value { get; set; }
}
```

**Step 2: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_MostDerivedInterfaceWins" -v n`
Expected: PASS (interface depth sorting should handle this)

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "test: add interface property shadowing test for cache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 1.8: Test Cache Thread Safety and Singleton Behavior

**Files:**
- Modify: `src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs`

**Step 1: Write test for cache singleton behavior**

Add to `SubjectPropertyMetadataCacheTests.cs`:

```csharp
[Fact]
public void Get_ReturnsSameInstance_ForSameType()
{
    // Act
    var result1 = SubjectPropertyMetadataCache.Get<SimpleSubject>();
    var result2 = SubjectPropertyMetadataCache.Get<SimpleSubject>();

    // Assert
    Assert.Same(result1, result2);
}

[Fact]
public void Get_ReturnsDifferentInstances_ForDifferentTypes()
{
    // Act
    var result1 = SubjectPropertyMetadataCache.Get<SimpleSubject>();
    var result2 = SubjectPropertyMetadataCache.Get<EmptySubject>();

    // Assert
    Assert.NotSame(result1, result2);
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectPropertyMetadataCacheTests.Get_Returns" -v n`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tests/SubjectPropertyMetadataCacheTests.cs
git commit -m "test: add cache singleton and thread safety tests

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Phase 2: Update Generator

### Task 2.1: Add EmitPropertyAccessors Method

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`

**Step 1: Read current SubjectCodeGenerator**

```bash
cat src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
```

**Step 2: Add EmitPropertyAccessors method**

Add after `EmitDefaultProperties` method:

```csharp
private static void EmitPropertyAccessors(StringBuilder builder, SubjectMetadata metadata)
{
    builder.AppendLine();
    builder.AppendLine("        [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
    builder.AppendLine("        internal static (global::System.Func<global::Namotion.Interceptor.IInterceptorSubject, object?>?, global::System.Action<global::Namotion.Interceptor.IInterceptorSubject, object?>?)");
    builder.AppendLine("            __GetPropertyAccessors(string name) => name switch");
    builder.AppendLine("        {");

    foreach (var property in metadata.Properties)
    {
        var typeCast = property.IsFromInterface && property.InterfaceTypeName != null
            ? $"global::{property.InterfaceTypeName}"
            : metadata.ClassName;

        var getter = property.HasGetter
            ? $"static o => (({typeCast})o).{property.Name}"
            : "null";

        var setter = property.HasSetter && !property.IsFromInterface
            ? $"static (o, v) => (({metadata.ClassName})o).{property.Name} = ({property.FullTypeName})v!"
            : "null";

        builder.AppendLine($"            \"{property.Name}\" => ({getter}, {setter}),");
    }

    builder.AppendLine("            _ => (null, null)");
    builder.AppendLine("        };");
}
```

**Step 3: Build to verify syntax**

Run: `dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
git commit -m "feat: add EmitPropertyAccessors method to generator

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 2.2: Update EmitInterceptorSubjectImplementation

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`

**Step 1: Read current EmitInterceptorSubjectImplementation**

Find and review the current implementation.

**Step 2: Update to use cache**

Change the Properties implementation from:
```csharp
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
    _properties ?? DefaultProperties;
```

To:
```csharp
[global::System.Text.Json.Serialization.JsonIgnore]
global::System.Collections.Generic.IReadOnlyDictionary<string, global::Namotion.Interceptor.SubjectPropertyMetadata> global::Namotion.Interceptor.IInterceptorSubject.Properties =>
    _properties ?? global::Namotion.Interceptor.SubjectPropertyMetadataCache.Get<{metadata.ClassName}>();
```

Also update `AddProperties` method:
```csharp
void global::Namotion.Interceptor.IInterceptorSubject.AddProperties(params global::System.Collections.Generic.IEnumerable<global::Namotion.Interceptor.SubjectPropertyMetadata> properties)
{
    _properties = (_properties ?? global::Namotion.Interceptor.SubjectPropertyMetadataCache.Get<{metadata.ClassName}>())
        .Concat(properties.Select(p => new global::System.Collections.Generic.KeyValuePair<string, global::Namotion.Interceptor.SubjectPropertyMetadata>(p.Name, p)))
#if NET8_0_OR_GREATER
        .ToFrozenDictionary();
#else
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
#endif
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
git commit -m "feat: update EmitInterceptorSubjectImplementation to use cache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 2.3: Emit [Intercepted] Attribute on Generated Properties

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`

**Step 1: Find EmitProperties method**

Locate where property implementations are emitted.

**Step 2: Add [Intercepted] attribute**

In the property emission, add before each generated partial property:
```csharp
[global::Namotion.Interceptor.Attributes.Intercepted]
```

The generated property should look like:
```csharp
[global::Namotion.Interceptor.Attributes.Intercepted]
public partial string FirstName
{
    get => GetPropertyValue<string>(nameof(FirstName), static o => ((Person)o)._FirstName);
    set
    {
        // ...
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
git commit -m "feat: emit [Intercepted] attribute on generated partial properties

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 2.4: Update Generate Method and Remove EmitDefaultProperties

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`

**Step 1: Update Generate method**

Change the `Generate` method to:
1. Remove call to `EmitDefaultProperties(builder, metadata)`
2. Add call to `EmitPropertyAccessors(builder, metadata)`

**Step 2: Remove EmitDefaultProperties method**

Delete the entire `EmitDefaultProperties` method (approximately lines 164-224).

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
git commit -m "refactor: replace EmitDefaultProperties with EmitPropertyAccessors

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 2.5: Build Solution and Fix Compilation Errors

**Files:**
- May need to modify multiple files depending on errors

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: May have errors from tests using `DefaultProperties`

**Step 2: Fix any compilation errors**

Document and fix each error. Common expected errors:
- Tests referencing `Type.DefaultProperties` need to change to `SubjectPropertyMetadataCache.Get<Type>()`
- Any code using `DefaultProperties` static property

**Step 3: Build again to verify all errors fixed**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add -A
git commit -m "fix: update code to use SubjectPropertyMetadataCache instead of DefaultProperties

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Phase 3: Update Tests and Snapshots

### Task 3.1: Update Test Files Using DefaultProperties

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyBehaviorTests.cs`
- Modify: Any other test files referencing `DefaultProperties`

**Step 1: Find all usages of DefaultProperties**

Run: `grep -r "DefaultProperties" src/ --include="*.cs"`

**Step 2: Update each usage**

Change pattern:
```csharp
// Before
var props = SomeType.DefaultProperties;

// After
var props = SubjectPropertyMetadataCache.Get<SomeType>();
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: migrate tests from DefaultProperties to SubjectPropertyMetadataCache

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 3.2: Regenerate Snapshot Tests

**Files:**
- All `.verified.txt` files in `src/Namotion.Interceptor.Generator.Tests/`

**Step 1: Run tests to identify failing snapshots**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests -v n`
Expected: Many snapshot tests will fail due to changed generated code

**Step 2: Accept new snapshots**

For each failing test, review the `.received.txt` file and if correct, rename to `.verified.txt`.

Or use the Verify library's auto-accept:
```bash
# Set environment variable to auto-accept
export DiffEngine_AcceptAll=true
dotnet test src/Namotion.Interceptor.Generator.Tests
```

**Step 3: Review generated code changes**

Verify that:
- `DefaultProperties` static property is gone
- `__GetPropertyAccessors` switch expression is present
- `[Intercepted]` attribute is on generated properties
- `IInterceptorSubject.Properties` uses cache

**Step 4: Commit**

```bash
git add -A
git commit -m "test: regenerate verified snapshots with new generator output

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

### Task 3.3: Run Full Test Suite

**Files:**
- All test projects

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx -v n`
Expected: All tests pass

**Step 2: If tests fail, debug and fix**

For each failing test:
1. Identify the cause
2. Fix the issue
3. Re-run the specific test
4. Continue until all pass

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve test failures after migration

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Phase 4: Cleanup and Validation

### Task 4.1: Run Benchmarks

**Files:**
- `src/Namotion.Interceptor.Benchmarks/`

**Step 1: Run benchmarks**

Run: `dotnet run --project src/Namotion.Interceptor.Benchmarks -c Release`

**Step 2: Document results**

Compare property access performance before/after. The cache lookup should be O(1) after first access.

**Step 3: No commit needed** (benchmark results are informational)

---

### Task 4.2: Final Test Run

**Step 1: Clean and rebuild**

Run: `dotnet clean src/Namotion.Interceptor.slnx && dotnet build src/Namotion.Interceptor.slnx`

**Step 2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx -v n`
Expected: All tests pass

**Step 3: Verify no warnings**

Check build output for any new warnings.

---

### Task 4.3: Create Summary Commit

**Step 1: Review all changes**

Run: `git log --oneline master..HEAD`

**Step 2: If needed, create squash commit or keep as-is**

The individual commits provide good history. Optionally squash for cleaner PR.

---

## Summary

This plan implements the runtime property metadata cache in 4 phases:

1. **Phase 1** (Tasks 1.1-1.8): Create `SubjectPropertyMetadataCache` with inheritance walking, interface default detection, and fallback accessors
2. **Phase 2** (Tasks 2.1-2.5): Update generator to emit `__GetPropertyAccessors` instead of `DefaultProperties`
3. **Phase 3** (Tasks 3.1-3.3): Update tests and regenerate snapshots
4. **Phase 4** (Tasks 4.1-4.3): Validate with benchmarks and final test run

**Key files created:**
- `src/Namotion.Interceptor/Attributes/InterceptedAttribute.cs`
- `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`

**Key files modified:**
- `src/Namotion.Interceptor/Namotion.Interceptor.csproj` (multi-target)
- `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` (major refactor)
- All test files using `DefaultProperties`
- All `.verified.txt` snapshot files

**Expected benefits:**
- Generated code reduced from ~10 lines to ~1 line per property
- Complex edge case handling moved to well-tested runtime reflection
- Better support for future C# features without generator changes
