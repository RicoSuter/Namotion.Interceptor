# Runtime Property Metadata Cache Design (v2)

> This design builds on the refactored generator from PR #182 (`feature/generator-default-interface-props` branch).

## Design Review Decisions

*Reviewed 2026-02-03*

| Decision | Outcome |
|----------|---------|
| Overall approach | ✅ Validated - move property discovery to runtime reflection |
| Intercepted property detection | Use `[Intercepted]` attribute (not backing field heuristic) |
| .NET Standard 2.0 compatibility | Multi-target `netstandard2.0;net8.0` for max perf |
| Interface shadowing | Sort interfaces by depth (most-derived first) |
| Redundant cache builds | Accept (rare under contention, simpler code) |
| Property ordering | Not a concern currently |
| Boxing in accessors | Keep for now (see [#185](https://github.com/RicoSuter/Namotion.Interceptor/issues/185)) |

---

## Overview

Replace the source-generated `DefaultProperties` dictionary with a runtime cache that builds property metadata on first access. The generator will only emit minimal delegate accessors via a switch expression, while the cache handles reflection, inheritance, and dictionary construction.

## Motivation

### The Core Problem

The generator currently implements complex logic to handle many edge cases:

- Base class property chaining
- Interface default implementations
- Diamond inheritance deduplication
- Virtual/override resolution
- Explicit interface implementations
- Attribute inheritance
- And more...

Each new C# feature or edge case requires modifying the generator. PR #179 added interface attribute inheritance. PR #182 added interface default implementations. Each added significant complexity.

### The Solution

**Move property discovery to runtime reflection.** The .NET runtime already handles all these edge cases correctly. Instead of reimplementing this logic in the generator, we leverage reflection at startup (cached per type).

**The generator becomes minimal** - it only generates:
1. Property implementations (backing fields + intercepted get/set)
2. Accessor delegates for the cache (one line per property)
3. `IInterceptorSubject` boilerplate

---

## What We Get "For Free" with Runtime Reflection

### Property Discovery & Metadata

| Feature | Generator Challenge | Reflection Solution |
|---------|---------------------|---------------------|
| Attribute inheritance from base classes | Walk base types in Roslyn | `GetCustomAttributes(inherit: true)` |
| Attribute inheritance from interfaces | PR #179 added special handling | Built-in with proper enumeration |
| Nullable annotations | Complex Roslyn analysis | `NullabilityInfoContext` (.NET 6+) |
| Property order consistency | Depends on syntax tree traversal | Deterministic metadata order |
| Cross-assembly base classes | Need compilation references | Just works |

### C# Language Features

| Feature | Generator Challenge | Reflection Solution |
|---------|---------------------|---------------------|
| Covariant return types (C# 9) | Track override return types | `PropertyInfo.PropertyType` is correct |
| Records with init properties | Detect synthesized members | `SetMethod?.IsInitOnly` |
| `new` hiding properties | Detect `new` modifier | `DeclaringType` tells you which level |
| Shadowed properties | Ambiguous name resolution | `GetProperties(DeclaredOnly)` + walk hierarchy |
| Primary constructors (C# 12) | Parameter vs property confusion | Properties are clear in reflection |
| Required members (C# 11) | `required` modifier detection | `RequiredMemberAttribute` (.NET 7+) |

### Interface Handling

| Feature | Generator Challenge | Reflection Solution |
|---------|---------------------|---------------------|
| Explicit interface implementations | `string IFoo.Name` syntax parsing | `GetInterfaceMap()` |
| Same property from multiple interfaces | Diamond + deduplication | `GetInterfaces()` + dedup naturally |
| Interface re-implementation | Track which interface level | `GetInterfaceMap().TargetMethods` |
| Default impl with explicit override | Which takes precedence? | Runtime knows the answer |
| Multiple interface hierarchies | Complex graph traversal | `AllInterfaces` property |

### Edge Cases the Generator Currently Handles

| Edge Case | Current Generator Handling | With Runtime Cache |
|-----------|---------------------------|-------------------|
| Base class properties | `.Concat(BaseClass.DefaultProperties)` | `Type.BaseType` reflection |
| Interface default impls | PR #182 added ~100 lines | `GetInterfaces()` + `IsAbstract` check |
| Multiple interface inheritance | Manual deduplication | Reflection handles automatically |
| Diamond inheritance | `processedPropertyNames` HashSet | Reflection handles automatically |
| Virtual/override properties | Special modifier detection | Reflection returns correct member |
| Explicit interface impls | Not fully handled | `GetInterfaceMap()` |
| Nested classes | Containing type tracking | Reflection handles automatically |
| Partial classes | Merge across syntax trees | Reflection sees unified type |
| Protected/internal props | Access modifier parsing | `PropertyInfo.GetGetMethod(true)` |
| Init-only properties | `HasInit` detection | `SetMethod?.IsInitOnly` |
| Property attributes | Limited in generator | `GetCustomAttributes()` |
| Generic base classes | Special type formatting | Reflection handles automatically |

### Future-Proofing

| Feature | Generator Challenge | Reflection Solution |
|---------|---------------------|---------------------|
| Future C# property features | Update generator for each | Likely works automatically |
| New access modifiers | Parse new syntax | API evolves with runtime |
| New property modifiers | Update Roslyn analysis | Reflected in PropertyInfo |

---

## Records Support

### What Records Get "For Free"

| Feature | Benefit |
|---------|---------|
| Non-partial record properties | Appear in `Properties` dictionary automatically |
| `init` accessors | Correctly detected as read-only for external access |
| Synthesized `EqualityContract` | Can be filtered (or included if needed) |
| Record inheritance | Base record properties discovered automatically |
| Positional properties | Properties from primary constructor discovered |

### Limitation: Partial Properties Still Required for Interception

For records with primary constructor:
```csharp
public partial record Person(string FirstName, string LastName);
```

These properties are **compiler-synthesized** - NOT partial. They appear in the metadata but are NOT intercepted.

To intercept record properties:
```csharp
[InterceptorSubject]
public partial record Person
{
    public partial string FirstName { get; set; }  // Intercepted
    public partial string LastName { get; set; }   // Intercepted
}
```

**This is a fundamental limitation of C# partial properties, not our design.**

---

## Architecture: What Goes Where

### Generator Responsibilities (Still Required)

```csharp
// 1. Backing fields
private string _FirstName;

// 2. Property implementation with interception (marked with [Intercepted] attribute)
[Intercepted]
public partial string FirstName
{
    get => GetPropertyValue<string>(nameof(FirstName), static o => ((Person)o)._FirstName);
    set
    {
        var newValue = value;
        var cancel = false;
        OnFirstNameChanging(ref newValue, ref cancel);
        if (!cancel && SetPropertyValue(...))
        {
            OnFirstNameChanged(_FirstName);
            RaisePropertyChanged(nameof(FirstName));
        }
    }
}

// 3. Partial hooks
partial void OnFirstNameChanging(ref string newValue, ref bool cancel);
partial void OnFirstNameChanged(string newValue);

// 4. Accessor delegates for cache (NEW - replaces DefaultProperties)
internal static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
    __GetPropertyAccessors(string name) => name switch
{
    "FirstName" => (static o => ((Person)o).FirstName, static (o, v) => ((Person)o).FirstName = (string)v!),
    "LastName" => (static o => ((Person)o).LastName, static (o, v) => ((Person)o).LastName = (string)v!),
    "FullName" => (static o => ((IHasFullName)o).FullName, null),  // Interface default
    _ => (null, null)
};

// 5. IInterceptorSubject boilerplate (simplified)
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
    _properties ?? SubjectPropertyMetadataCache.Get<Person>();
```

### Runtime Cache Responsibilities (NEW)

- Property discovery via reflection
- Inheritance chain walking
- Interface default implementation detection
- Attribute collection
- PropertyInfo caching
- Delegate retrieval from generated `__GetPropertyAccessors`
- Fallback delegate creation for non-generated types
- Building `FrozenDictionary<string, SubjectPropertyMetadata>` (.NET 8+) or `Dictionary` (.NET Standard 2.0)

### Summary Table

| Concern | Handled By | Reason |
|---------|------------|--------|
| Property **discovery** | Runtime cache | Reflection handles all edge cases |
| Property **implementation** | Generator | C# partial properties require compile-time |
| Property **accessors** for metadata | Generator | AOT-compatible fast delegates |
| Inheritance resolution | Runtime cache | Reflection is authoritative |
| Interface handling | Runtime cache | `GetInterfaceMap()` is complete |
| Attribute discovery | Runtime cache | `GetCustomAttributes()` with inheritance |

---

## Current State (PR #182 Branch)

The generator is already refactored into clean components:

- **`SubjectMetadataExtractor`** - Extracts metadata from Roslyn symbols
- **`SubjectCodeGenerator`** - Generates C# code from metadata
- **`PropertyMetadata`** - Already has `IsFromInterface` and `InterfaceTypeName`

---

## Proposed Changes

### 1. New Generated Code (~1 line per property)

Replace `EmitDefaultProperties()` with `EmitPropertyAccessors()`:

```csharp
internal static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
    __GetPropertyAccessors(string name) => name switch
{
    "FirstName" => (static o => ((Person)o).FirstName, static (o, v) => ((Person)o).FirstName = (string)v!),
    "LastName" => (static o => ((Person)o).LastName, static (o, v) => ((Person)o).LastName = (string)v!),
    "FullName" => (static o => ((IHasFullName)o).FullName, null),  // Interface default impl
    _ => (null, null)
};
```

### 2. Updated `IInterceptorSubject.Properties` Implementation

```csharp
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
    _properties ?? SubjectPropertyMetadataCache.Get<Person>();
```

### 3. Runtime Cache Implementation

> **Multi-targeting:** The core library will target `netstandard2.0;net8.0`. On .NET 8+, use `FrozenDictionary` for maximum lookup performance. On .NET Standard 2.0, use `Dictionary` wrapped as `IReadOnlyDictionary`.

```csharp
public static class SubjectPropertyMetadataCache
{
#if NET8_0_OR_GREATER
    private static readonly ConcurrentDictionary<Type, FrozenDictionary<string, SubjectPropertyMetadata>> Cache = new();
#else
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, SubjectPropertyMetadata>> Cache = new();
#endif

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
                    isIntercepted: property.GetCustomAttribute<InterceptedAttribute>() != null,
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
            method.Invoke(null, [propertyName])!;
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

        try
        {
            // Fast path - works on JIT runtimes
            var param = Expression.Parameter(typeof(IInterceptorSubject));
            var cast = Expression.Convert(param, property.DeclaringType!);
            var access = Expression.Property(cast, property);
            var boxed = Expression.Convert(access, typeof(object));
            return Expression.Lambda<Func<IInterceptorSubject, object?>>(boxed, param).Compile();
        }
        catch (PlatformNotSupportedException)
        {
            // AOT fallback - slower but works
            return subject => property.GetValue(subject);
        }
    }

    private static Action<IInterceptorSubject, object?>? CreateFallbackSetter(PropertyInfo property)
    {
        var setMethod = property.GetSetMethod(true);
        if (setMethod == null) return null;

        try
        {
            var subjectParam = Expression.Parameter(typeof(IInterceptorSubject));
            var valueParam = Expression.Parameter(typeof(object));
            var cast = Expression.Convert(subjectParam, property.DeclaringType!);
            var valueCast = Expression.Convert(valueParam, property.PropertyType);
            var assign = Expression.Assign(Expression.Property(cast, property), valueCast);
            return Expression.Lambda<Action<IInterceptorSubject, object?>>(assign, subjectParam, valueParam).Compile();
        }
        catch (PlatformNotSupportedException)
        {
            return (subject, value) => property.SetValue(subject, value);
        }
    }

    private static IEnumerable<Type> GetInterfacesSortedByDepth(Type type)
    {
        var interfaces = type.GetInterfaces();
        var interfaceSet = new HashSet<Type>(interfaces);

        // Depth = count of ancestor interfaces also in our set
        // Most derived (highest depth) first - ensures shadowing works correctly
        return interfaces.OrderByDescending(iface =>
            iface.GetInterfaces().Count(i => interfaceSet.Contains(i)));
    }

    private static void AddInterfaceDefaultImplementations(
        Type type,
        Dictionary<string, SubjectPropertyMetadata> result)
    {
        // Sort interfaces by depth so most-derived wins for shadowed properties
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
                    (getterDelegate, setterDelegate) = CreateFallbackAccessors(property);
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

}
```

---

## Code Size Comparison

### Before (per property in `DefaultProperties`):
```csharp
{
    "FirstName",
    new SubjectPropertyMetadata(
        typeof(Person).GetProperty(nameof(FirstName), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,
        (o) => ((Person)o).FirstName,
        (o, v) => ((Person)o).FirstName = (string)v,
        isIntercepted: true,
        isDynamic: false)
},
```
**~8-10 lines per property**

### After (per property in `__GetPropertyAccessors`):
```csharp
"FirstName" => (static o => ((Person)o).FirstName, static (o, v) => ((Person)o).FirstName = (string)v!),
```
**~1 line per property**

### Summary:

| Properties | Current `DefaultProperties` | New `__GetPropertyAccessors` |
|------------|----------------------------|------------------------------|
| 5 | ~50-60 lines | ~7 lines |
| 10 | ~100-120 lines | ~12 lines |
| 20 | ~200-240 lines | ~22 lines |

---

## Performance Characteristics

| Scenario | Cache Build (one-time) | Delegate Calls |
|----------|------------------------|----------------|
| Generated type + JIT | Slow (reflection) | Fast |
| Generated type + Native AOT | Slow (reflection) | Fast |
| Non-generated type + JIT | Slow (Expression.Compile) | Fast |
| Non-generated type + Native AOT | Slow (reflection) | Slower (~50-100ns) |

**Key points:**
- Cache build happens once per type at first access
- After startup, all property metadata access is instant dictionary lookup
- Generated types have fast delegates on ALL platforms (including Native AOT)
- Only non-generated types (DynamicSubject, manual implementations) have slower AOT fallback

---

## Generator Changes

### Modify `SubjectCodeGenerator`

#### Add `EmitPropertyAccessors`:

```csharp
private static void EmitPropertyAccessors(StringBuilder builder, SubjectMetadata metadata)
{
    builder.AppendLine("        internal static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)");
    builder.AppendLine("            __GetPropertyAccessors(string name) => name switch");
    builder.AppendLine("        {");

    foreach (var property in metadata.Properties)
    {
        var typeCast = property.IsFromInterface
            ? property.InterfaceTypeName
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
    builder.AppendLine();
}
```

#### Update `EmitInterceptorSubjectImplementation`:

```csharp
private static void EmitInterceptorSubjectImplementation(StringBuilder builder, SubjectMetadata metadata)
{
    builder.Append($"""
            private IInterceptorExecutor? _context;
            private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

            [JsonIgnore]
            IInterceptorSubjectContext IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this);

            [JsonIgnore]
            ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data {{ get; }} = new();

            [JsonIgnore]
            IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
                _properties ?? SubjectPropertyMetadataCache.Get<{metadata.ClassName}>();

            [JsonIgnore]
            object IInterceptorSubject.SyncRoot {{ get; }} = new object();

            void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
            {{
                _properties = (_properties ?? SubjectPropertyMetadataCache.Get<{metadata.ClassName}>())
                    .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                    .ToFrozenDictionary();
            }}


        """);
}
```

#### Update `Generate` method:

```csharp
public static string Generate(SubjectMetadata metadata)
{
    var builder = new StringBuilder();

    EmitFileHeader(builder);
    EmitNamespaceOpening(builder, metadata.NamespaceName);
    EmitContainingTypeOpening(builder, metadata.ContainingTypes);
    EmitClassDeclaration(builder, metadata);
    EmitNotifyPropertyChangedImplementation(builder, metadata.BaseClassHasInpc);
    EmitInterceptorSubjectImplementation(builder, metadata);  // Updated
    EmitPropertyAccessors(builder, metadata);                  // NEW - replaces EmitDefaultProperties
    EmitConstructors(builder, metadata);
    EmitProperties(builder, metadata);
    EmitMethods(builder, metadata);
    EmitHelperMethods(builder);
    EmitClassClosing(builder);
    EmitContainingTypeClosing(builder, metadata.ContainingTypes);
    EmitNamespaceClosing(builder);

    return builder.ToString();
}
```

#### Remove:
- `EmitDefaultProperties()` method entirely

---

## Implementation Steps

### Phase 1: Add Runtime Cache
1. Multi-target core library: `<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>`
2. Create `InterceptedAttribute` class in `Namotion.Interceptor.Attributes` (similar to `DerivedAttribute`)
3. Create `SubjectPropertyMetadataCache` class in `Namotion.Interceptor` core library
4. Implement cache lookup with `Get<T>()` method
5. Implement metadata building with inheritance walking
6. Implement `GetInterfacesSortedByDepth()` for correct interface shadowing
7. Implement interface default implementation detection
8. Implement fallback accessor creation (Expression.Compile / PropertyInfo)
9. Add unit tests for cache behavior
10. Add unit test for interface property shadowing (most-derived wins)

### Phase 2: Update Generator
1. Add `EmitPropertyAccessors()` method to `SubjectCodeGenerator`
2. Update `EmitInterceptorSubjectImplementation()` to use cache
3. Emit `[Intercepted]` attribute on generated partial properties
4. Remove `EmitDefaultProperties()` method
5. Update `Generate()` to call new methods
6. Update snapshot tests

### Phase 3: Update Tests
1. Change `Type.DefaultProperties` → `SubjectPropertyMetadataCache.Get<Type>()`
2. Regenerate all `.verified.txt` snapshots
3. Add tests for new cache behavior

### Phase 4: Cleanup & Validation
1. Run full test suite
2. Update documentation
3. Run benchmarks to verify performance
4. Test Native AOT compatibility

---

## API Changes

### Removed
- `DefaultProperties` static property on generated classes

### Added
- `SubjectPropertyMetadataCache.Get<T>()` - public API for getting property metadata
- `__GetPropertyAccessors(string)` - internal generated method (implementation detail)

### Migration
```csharp
// Before
var props = Person.DefaultProperties;

// After
var props = SubjectPropertyMetadataCache.Get<Person>();

// Or via instance (unchanged):
var props = ((IInterceptorSubject)person).Properties;
```

---

## Files to Modify

1. **New file:** `src/Namotion.Interceptor/SubjectPropertyMetadataCache.cs`
2. **New file:** `src/Namotion.Interceptor/Attributes/InterceptedAttribute.cs` (similar to `DerivedAttribute`)
3. **Modify:** `src/Namotion.Interceptor/Namotion.Interceptor.csproj`
   - Change to multi-target: `<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>`
4. **Modify:** `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
   - Add `EmitPropertyAccessors()`
   - Update `EmitInterceptorSubjectImplementation()`
   - Remove `EmitDefaultProperties()`
   - Emit `[Intercepted]` attribute on generated properties
5. **Modify:** Test files using `DefaultProperties`
6. **Update:** All snapshot test files (`.verified.txt`)
7. **New tests:** Interface property shadowing (most-derived wins)

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Cache build takes too long | One-time per type, lazy initialization. Acceptable for app startup. |
| Thread-safety issues | `ConcurrentDictionary.GetOrAdd` is atomic |
| Breaking change for `DefaultProperties` | Document migration, provide `SubjectPropertyMetadataCache.Get<T>()` |
| AOT compatibility | Generated accessors are AOT-safe; fallback uses PropertyInfo |

---

## Open Questions (Resolved)

1. **Should `__GetPropertyAccessors` be hidden from IntelliSense?**
   - ✅ Yes, add `[EditorBrowsable(EditorBrowsableState.Never)]`

2. **Should we keep `DefaultProperties` as a thin wrapper?**
   - ✅ No - clean break, simpler generated code

3. **Naming: `__GetPropertyAccessors` with double underscore?**
   - ✅ Yes - indicates internal/generated code

---

## Future Optimizations

- **Avoid boxing in property accessors** - See [#185](https://github.com/RicoSuter/Namotion.Interceptor/issues/185) for a generic accessor pattern that avoids boxing for value types while remaining AOT-compatible.
