# Runtime Property Metadata Cache Design

## Overview

Replace the source-generated `DefaultProperties` dictionary with a runtime cache that builds property metadata on first access. The generator will only emit minimal delegate accessors, while the cache handles reflection, inheritance, and dictionary construction.

## Goals

1. **Simplify generated code** - Reduce ~50+ lines per class to ~10 lines
2. **Maintain performance** - Fast delegate calls on all platforms including Native AOT
3. **AOT compatibility** - Full support for .NET Native AOT with fast delegates
4. **Reduce generator complexity** - Move inheritance/interface handling to runtime

## Current State

The generator currently produces a large `DefaultProperties` dictionary per class:

```csharp
public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
    new Dictionary<string, SubjectPropertyMetadata>
    {
        {
            "FirstName",
            new SubjectPropertyMetadata(
                typeof(Person).GetProperty(nameof(FirstName), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,
                (o) => ((Person)o).FirstName,
                (o, v) => ((Person)o).FirstName = (string)v,
                isIntercepted: true,
                isDynamic: false)
        },
        // ... repeated for each property
    }
    .Concat(BaseClass.DefaultProperties)
    .ToFrozenDictionary();
```

**Problems:**
- Verbose (~8-10 lines per property)
- Generator handles inheritance chaining
- Generator handles interface properties
- Large amount of generated code

## Proposed Design

### 1. Generated Code (Minimal)

Each class generates only a compact accessor method:

```csharp
internal static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
    __GetPropertyAccessors(string name) => name switch
{
    "FirstName" => (static o => ((Person)o).FirstName, static (o, v) => ((Person)o).FirstName = (string)v!),
    "LastName" => (static o => ((Person)o).LastName, static (o, v) => ((Person)o).LastName = (string)v!),
    "FullName" => (static o => ((IHasFullName)o).FullName, null),  // Interface default implementation
    _ => (null, null)
};
```

And the Properties accessor changes to:

```csharp
[JsonIgnore]
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
    _properties ?? SubjectPropertyMetadataCache.Get<Person>();
```

### 2. Runtime Cache

New `SubjectPropertyMetadataCache` class in core library:

```csharp
public static class SubjectPropertyMetadataCache
{
    private static readonly ConcurrentDictionary<Type, FrozenDictionary<string, SubjectPropertyMetadata>> Cache = new();

    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> Get<T>()
        where T : IInterceptorSubject
    {
        return Cache.GetOrAdd(typeof(T), static type => BuildMetadata(type));
    }

    private static FrozenDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
    {
        var result = new Dictionary<string, SubjectPropertyMetadata>();

        // Walk inheritance chain (most derived first)
        var currentType = type;
        while (currentType != null && typeof(IInterceptorSubject).IsAssignableFrom(currentType))
        {
            foreach (var property in GetDeclaredProperties(currentType))
            {
                if (result.ContainsKey(property.Name)) continue; // Most derived wins

                var (getter, setter) = GetGeneratedAccessors(currentType, property.Name);
                if (getter == null && setter == null)
                {
                    (getter, setter) = CreateFallbackAccessors(property);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getter,
                    setter,
                    isIntercepted: IsIntercepted(property),
                    isDynamic: false);
            }
            currentType = currentType.BaseType;
        }

        // Add interface default implementations not already covered
        AddInterfaceDefaultImplementations(type, result);

        return result.ToFrozenDictionary();
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        GetGeneratedAccessors(Type type, string propertyName)
    {
        var method = type.GetMethod("__GetPropertyAccessors",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method == null) return (null, null);

        return ((Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?))
            method.Invoke(null, [propertyName])!;
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        CreateFallbackAccessors(PropertyInfo property)
    {
        // For non-generated types (DynamicSubject, manual implementations)
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
            // Fast path - works on JIT runtimes
            var subjectParam = Expression.Parameter(typeof(IInterceptorSubject));
            var valueParam = Expression.Parameter(typeof(object));
            var cast = Expression.Convert(subjectParam, property.DeclaringType!);
            var valueCast = Expression.Convert(valueParam, property.PropertyType);
            var assign = Expression.Assign(Expression.Property(cast, property), valueCast);
            return Expression.Lambda<Action<IInterceptorSubject, object?>>(assign, subjectParam, valueParam).Compile();
        }
        catch (PlatformNotSupportedException)
        {
            // AOT fallback - slower but works
            return (subject, value) => property.SetValue(subject, value);
        }
    }
}
```

### 3. Generator Changes

The generator needs to:

1. **Remove** `DefaultProperties` generation entirely

2. **Add** `__GetPropertyAccessors` method generation:
   - Include all partial properties declared in the class
   - Include interface default implementations the class inherits
   - Cast to interface type for interface default implementations

3. **Change** `IInterceptorSubject.Properties` to use cache:
   ```csharp
   IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties =>
       _properties ?? SubjectPropertyMetadataCache.Get<ClassName>();
   ```

### 4. What the Generator Includes in `__GetPropertyAccessors`

For each class, the generator emits accessors for:

1. **Partial properties declared in the class itself**
   ```csharp
   "FirstName" => (static o => ((Person)o).FirstName, static (o, v) => ((Person)o).FirstName = (string)v!),
   ```

2. **Interface default implementations**
   - Detect interfaces the class implements
   - Find properties with non-abstract getters/setters on the interface
   - Generate accessor that casts to interface type
   ```csharp
   "FullName" => (static o => ((IHasFullName)o).FullName, null),
   ```

**NOT included** (handled by cache at runtime):
- Base class properties (cache walks inheritance chain)
- PropertyInfo, attributes, type information (cache uses reflection)

### 5. Inheritance Handling

**Current (generator handles):**
```csharp
// In Teacher class
.Concat(Person.DefaultProperties)
```

**New (cache handles):**
```csharp
// Cache walks: Teacher → Person → object
// Calls __GetPropertyAccessors on each level
// Most derived property wins on name collision
```

### 6. Performance Characteristics

| Scenario | Cache Build (one-time) | Delegate Calls |
|----------|------------------------|----------------|
| Generated type + JIT | Slow (reflection) | Fast |
| Generated type + Native AOT | Slow (reflection) | Fast |
| Non-generated type + JIT | Slow (Expression.Compile) | Fast |
| Non-generated type + Native AOT | Slow (reflection) | Slower (~50-100ns) |

**Key insight:** Cache build is one-time at startup. All subsequent property metadata access is instant dictionary lookup. Delegate calls use pre-compiled lambdas (fast) for generated types on all platforms.

### 7. Code Size Comparison

| Properties | Current `DefaultProperties` | New `__GetPropertyAccessors` |
|------------|----------------------------|------------------------------|
| 5 | ~50-60 lines | ~7 lines |
| 10 | ~100-120 lines | ~12 lines |
| 20 | ~200-240 lines | ~22 lines |

## Implementation Steps

### Phase 1: Add Runtime Cache
1. Create `SubjectPropertyMetadataCache` class in core library
2. Implement cache lookup with `Get<T>()` method
3. Implement metadata building with inheritance walking
4. Implement fallback accessor creation (Expression.Compile / PropertyInfo)
5. Add unit tests for cache behavior

### Phase 2: Update Generator
1. Add `__GetPropertyAccessors` method generation
2. Include partial properties from class
3. Detect and include interface default implementations
4. Change `IInterceptorSubject.Properties` to use cache
5. Remove `DefaultProperties` generation entirely
6. Update snapshot tests

### Phase 3: Cleanup
1. Remove any code that references `DefaultProperties` statically
2. Update documentation
3. Run full test suite
4. Run benchmarks to verify performance

## API Changes

### Removed
- `DefaultProperties` static property on generated classes (was `public static`)

### Added
- `SubjectPropertyMetadataCache.Get<T>()` - public API for getting property metadata
- `__GetPropertyAccessors` - internal generated method (not part of public API)

### Migration
External code using `Person.DefaultProperties` should change to:
```csharp
// Before
var props = Person.DefaultProperties;

// After
var props = SubjectPropertyMetadataCache.Get<Person>();
// Or via instance:
var props = person.AsInterceptorSubject().Properties;
```

## Testing Strategy

1. **Unit tests for cache:**
   - Single class metadata building
   - Inheritance chain handling
   - Interface default implementation detection
   - Fallback accessor creation
   - Thread-safety of cache

2. **Generator snapshot tests:**
   - Update existing snapshots for new generated code
   - Add tests for interface default implementations

3. **Integration tests:**
   - Full property tracking still works
   - Derived properties still work
   - Registry still works
   - All existing tests pass

4. **Benchmark comparison:**
   - Compare cache build time
   - Compare delegate invocation time
   - Memory allocation comparison

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Cache build takes too long | Build is one-time, lazy per type. Acceptable for app startup. |
| Thread-safety issues | Use `ConcurrentDictionary.GetOrAdd` for atomic cache population |
| Interface default impl detection is complex | Well-tested runtime code, easier than generator logic |
| Breaking change for `DefaultProperties` users | Document migration path, property metadata still accessible via cache |

## Open Questions

1. Should `SubjectPropertyMetadataCache` be in core library or separate package?
   - **Recommendation:** Core library - it's fundamental infrastructure

2. Should we keep `DefaultProperties` as a thin wrapper for backwards compatibility?
   - **Recommendation:** No - clean break, document migration

3. Should `__GetPropertyAccessors` method name be configurable?
   - **Recommendation:** No - internal implementation detail, keep simple
