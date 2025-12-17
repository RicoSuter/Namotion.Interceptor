# Unified Connectors Architecture

## Overview

This document describes the architectural refactoring to unify path handling and introduce a common base interface for sources and servers. The key changes are:

1. **Simplify `IPathProvider`** to 3 core methods (segment-centric design)
2. Move path handling to `Namotion.Interceptor.Registry`
3. Move `[Children]` attribute to `Namotion.Interceptor.Registry`
4. Rename `Namotion.Interceptor.Sources` to `Namotion.Interceptor.Connectors`
5. Introduce `ISubjectConnector` as a base interface for sources (and optionally servers)

## Motivation

Currently, path handling code is duplicated across multiple packages:
- `HomeBlaze.Services` - SubjectPathResolver, ChildrenAttributeCache
- `Namotion.Interceptor.Sources/Paths` - ISourcePathProvider, PathExtensions, SourcePathAttribute
- MQTT and OPC UA packages have their own path-related code

The `[SourcePath]` attribute and `IsPropertyIncluded` are not source-specific - they're also used by servers (ASP.NET Core, MQTT server, OPC UA server). The `Updates/` folder containing `SubjectUpdate` is similarly used by both sources and servers.

## Compatibility with PR #114 (Transactions)

This design is fully compatible with the incoming transaction system in PR #114:
- **Transaction system stays in Tracking** - `SubjectTransactionInterceptor`, `TransactionMode`, etc. remain unchanged
- **Sources still implement `ISubjectSource`** - The `WriteChangesAsync` method signature is unchanged
- **`SubjectSourceBackgroundService`** - Groups changes by source and handles batch writes as before
- The `ISubjectConnector` base interface is additive and doesn't affect transaction semantics

---

## Simplified IPathProvider Interface

### Before (6 methods)
```csharp
public interface ISourcePathProvider
{
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    string? TryGetPropertySegment(RegisteredSubjectProperty property);
    string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty, object?)> propertiesInPath);
    IEnumerable<(string, object?)> ParsePathSegments(string path);
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
    RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string segment);
}
```

### After (3 methods)
```csharp
public interface IPathProvider
{
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    string GetPropertySegment(RegisteredSubjectProperty property);
    RegisteredSubjectProperty? GetPropertyFromSegment(RegisteredSubject subject, string segment);
}
```

### Why This Works

| Removed Method | Replacement | Reason |
|----------------|-------------|--------|
| `GetPropertyFullPath` | Extension method | Uses `GetPropertySegment` + separator from base class |
| `ParsePathSegments` | Internal helper in base class | Simple string split, no customization needed |
| `TryGetAttributeFromSegment` | Extension method | Never overridden, just iterates properties |

**Key insight**: Customization (like camelCase) happens at the **segment level**, not path level. This is cleaner and more composable.

---

## Target Architecture

### Package Structure

```
Namotion.Interceptor.Registry/
├── Abstractions/
│   ├── ISubjectRegistry.cs
│   ├── RegisteredSubject.cs
│   └── RegisteredSubjectProperty.cs
├── Attributes/
│   ├── ChildrenAttribute.cs          # NEW - moved from HomeBlaze
│   └── PathAttribute.cs              # RENAMED from SourcePathAttribute
├── Paths/                            # NEW - moved from Sources
│   ├── IPathProvider.cs              # SIMPLIFIED (3 methods)
│   ├── PathProviderBase.cs           # Has separator config, [Children] support
│   ├── DefaultPathProvider.cs
│   ├── JsonCamelCasePathProvider.cs
│   ├── AttributeBasedPathProvider.cs
│   ├── PathExtensions.cs             # BuildPath, ResolvePath, etc.
│   └── ChildrenAttributeCache.cs     # NEW - moved from HomeBlaze
└── ...existing files...

Namotion.Interceptor.Connectors/      # RENAMED from Sources
├── ISubjectConnector.cs              # NEW - base interface
├── Sources/
│   ├── ISubjectSource.cs             # Extends ISubjectConnector
│   ├── SubjectSourceBackgroundService.cs
│   ├── SubjectPropertyWriter.cs
│   ├── WriteRetryQueue.cs
│   └── Transactions/                 # From PR #114
│       ├── SourceTransactionWriter.cs
│       └── SourceTransactionWriteException.cs
├── Updates/
│   ├── SubjectUpdate.cs
│   ├── SubjectPropertyUpdate.cs
│   ├── ISubjectUpdateProcessor.cs
│   └── ...
├── ISubjectFactory.cs
├── DefaultSubjectFactory.cs
└── Resilience/
    └── CircuitBreaker.cs
```

### Interface Definitions

```csharp
// In Namotion.Interceptor.Registry/Paths/IPathProvider.cs
namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Maps between subject properties and external path segments.
/// Used by both sources (inbound sync) and servers (outbound exposure).
/// </summary>
public interface IPathProvider
{
    /// <summary>
    /// Should this property be included in paths?
    /// </summary>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Get the path segment for a property.
    /// Override for camelCase, custom naming, [Path] attribute, etc.
    /// </summary>
    string GetPropertySegment(RegisteredSubjectProperty property);

    /// <summary>
    /// Find a property by its path segment.
    /// Override for camelCase conversion, [Children] fallback, etc.
    /// </summary>
    RegisteredSubjectProperty? GetPropertyFromSegment(RegisteredSubject subject, string segment);
}
```

```csharp
// In Namotion.Interceptor.Registry/Paths/PathProviderBase.cs
namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Base implementation with configurable separators and [Children] support.
/// </summary>
public abstract class PathProviderBase : IPathProvider
{
    // Configuration - exposed by implementation, not on interface
    public virtual char PathSeparator => '.';
    public virtual char IndexOpen => '[';
    public virtual char IndexClose => ']';

    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

    public virtual string GetPropertySegment(RegisteredSubjectProperty property)
        => property.BrowseName;

    public virtual RegisteredSubjectProperty? GetPropertyFromSegment(
        RegisteredSubject subject, string segment)
    {
        // 1. Direct property lookup by segment name
        foreach (var property in subject.Properties)
        {
            if (GetPropertySegment(property) == segment)
                return property;
        }

        // 2. [Children] fallback - segment is a dictionary key
        var childrenPropName = ChildrenAttributeCache.GetChildrenPropertyName(subject.Subject.GetType());
        if (childrenPropName != null)
        {
            // Return the Children property - caller uses segment as dictionary key
            return subject.TryGetProperty(childrenPropName);
        }

        return null;
    }

    // --- Internal helpers for extension methods ---

    internal string BuildFullPath(IEnumerable<(RegisteredSubjectProperty prop, object? index)> properties)
    {
        var sb = new StringBuilder();
        foreach (var (prop, index) in properties)
        {
            if (sb.Length > 0) sb.Append(PathSeparator);
            sb.Append(GetPropertySegment(prop));
            if (index != null)
                sb.Append(IndexOpen).Append(index).Append(IndexClose);
        }
        return sb.ToString();
    }

    internal IEnumerable<(string segment, object? index)> ParseFullPath(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;

        foreach (var part in path.Split(PathSeparator))
        {
            var bracketIdx = part.IndexOf(IndexOpen);
            if (bracketIdx < 0)
            {
                yield return (part, null);
            }
            else
            {
                var name = part[..bracketIdx];
                var closeIdx = part.IndexOf(IndexClose);
                var indexStr = part[(bracketIdx + 1)..closeIdx];
                object index = int.TryParse(indexStr, out var i) ? i : indexStr;
                yield return (name, index);
            }
        }
    }
}
```

```csharp
// In Namotion.Interceptor.Connectors/ISubjectConnector.cs
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base interface for components that connect subjects to external systems.
/// </summary>
public interface ISubjectConnector
{
    IInterceptorSubject RootSubject { get; }
    IPathProvider PathProvider { get; }
}
```

```csharp
// In Namotion.Interceptor.Connectors/Sources/ISubjectSource.cs
namespace Namotion.Interceptor.Connectors.Sources;

/// <summary>
/// Represents a source that synchronizes data FROM an external system to a subject.
/// </summary>
public interface ISubjectSource : ISubjectConnector
{
    int WriteBatchSize { get; }
    Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);
    ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);
}
```

### JsonCamelCasePathProvider Example

```csharp
public class JsonCamelCasePathProvider : PathProviderBase
{
    public static JsonCamelCasePathProvider Instance { get; } = new();

    // Segment output: "Temperature" → "temperature"
    public override string GetPropertySegment(RegisteredSubjectProperty property)
        => ToCamelCase(property.BrowseName);

    // Segment input: "temperature" → lookup "Temperature"
    public override RegisteredSubjectProperty? GetPropertyFromSegment(
        RegisteredSubject subject, string segment)
    {
        return base.GetPropertyFromSegment(subject, ToPascalCase(segment));
    }

    private static string ToCamelCase(string s) =>
        s.Length > 1 ? char.ToLowerInvariant(s[0]) + s[1..] : s.ToLowerInvariant();

    private static string ToPascalCase(string s) =>
        s.Length > 1 ? char.ToUpperInvariant(s[0]) + s[1..] : s.ToUpperInvariant();
}
```

### Attributes

```csharp
// In Namotion.Interceptor.Registry/Attributes/PathAttribute.cs
namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Specifies a custom path segment for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PathAttribute : Attribute
{
    public PathAttribute(string path) => Path = path;
    public string Path { get; }
}
```

```csharp
// In Namotion.Interceptor.Registry/Attributes/ChildrenAttribute.cs
namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Marks a dictionary property as the default child container for path resolution.
/// Child keys become directly accessible in paths without the property name.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChildrenAttribute : Attribute { }
```

---

## Step-by-Step Implementation Plan

### Phase 1: Add to Registry (Non-breaking)

**Step 1.1: Add [Children] attribute to Registry**
- Create `src/Namotion.Interceptor.Registry/Attributes/ChildrenAttribute.cs`
- Create `src/Namotion.Interceptor.Registry/Paths/ChildrenAttributeCache.cs`

**Step 1.2: Add [Path] attribute to Registry**
- Create `src/Namotion.Interceptor.Registry/Attributes/PathAttribute.cs`

**Step 1.3: Add simplified IPathProvider to Registry**
- Create `src/Namotion.Interceptor.Registry/Paths/IPathProvider.cs` (3 methods)
- Create `src/Namotion.Interceptor.Registry/Paths/PathProviderBase.cs` (with [Children] support)
- Create `src/Namotion.Interceptor.Registry/Paths/DefaultPathProvider.cs`
- Create `src/Namotion.Interceptor.Registry/Paths/JsonCamelCasePathProvider.cs`
- Create `src/Namotion.Interceptor.Registry/Paths/AttributeBasedPathProvider.cs`

**Step 1.4: Add PathExtensions to Registry**
- Create `src/Namotion.Interceptor.Registry/Paths/PathExtensions.cs`
- Methods: `TryGetPath`, `TryGetPropertyFromPath`, `GetPropertiesFromPaths`, etc.
- These call `PathProviderBase` internal helpers for full path operations

### Phase 2: Create Connectors Package

**Step 2.1: Create new project**
- Create `src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
- Add reference to `Namotion.Interceptor.Registry`

**Step 2.2: Add ISubjectConnector**
- Create `src/Namotion.Interceptor.Connectors/ISubjectConnector.cs`

**Step 2.3: Move Sources content**
- Move `ISubjectSource.cs` → `Sources/ISubjectSource.cs` (extend ISubjectConnector)
- Move `SubjectSourceBackgroundService.cs` → `Sources/`
- Move `SubjectPropertyWriter.cs` → `Sources/`
- Move `WriteRetryQueue.cs` → `Sources/`
- Move `Transactions/` → `Sources/Transactions/` (from PR #114)

**Step 2.4: Move Updates content**
- Move entire `Updates/` folder as-is

**Step 2.5: Move other files**
- Move `ISubjectFactory.cs`, `DefaultSubjectFactory.cs`
- Move `Resilience/CircuitBreaker.cs`
- Move `ChangeQueueProcessor.cs`

### Phase 3: Update Consumers

**Step 3.1: Update MQTT package**
- Update imports from `Sources` → `Connectors`
- Update imports from `Sources.Paths` → `Registry.Paths`
- Update `MqttSubjectClientSource` to implement `ISubjectConnector`
- Update `MqttClientConfiguration.PathProvider` type

**Step 3.2: Update OPC UA package**
- Same pattern as MQTT

**Step 3.3: Update ASP.NET Core package**
- Update imports for path providers
- Update imports for Updates

**Step 3.4: Update HomeBlaze**
- Remove `HomeBlaze.Abstractions.Attributes.ChildrenAttribute` (use Registry)
- Remove `HomeBlaze.Abstractions.Attributes.ChildrenAttributeCache` (use Registry)
- Update `SubjectPathResolver` to use `PathProviderBase` from Registry
- Update all imports

### Phase 4: Deprecate Old Package

**Step 4.1: Add forwarding types (optional)**
- In old `Namotion.Interceptor.Sources`, add `[Obsolete]` type forwards to new locations
- Or just delete and let consumers update

**Step 4.2: Update solution**
- Remove `Namotion.Interceptor.Sources` project
- Update solution file

### Phase 5: Update Documentation

**Step 5.1: Rename sources.md**
- Rename `docs/sources.md` → `docs/connectors.md`
- Update content to reflect new architecture (ISubjectConnector, IPathProvider)
- Add [Children] attribute documentation
- Add server vs source clarification

**Step 5.2: Update other docs**
- Update `docs/tracking.md` if it references Sources
- Update `docs/registry.md` to include Paths documentation
- Update README.md if it references old package names

**Step 5.3: Update code comments**
- Search for "ISourcePathProvider" in comments
- Search for "SourcePath" in comments
- Update to reflect new naming

---

## Breaking Changes Summary

| Before | After |
|--------|-------|
| `Namotion.Interceptor.Sources` | `Namotion.Interceptor.Connectors` |
| `Namotion.Interceptor.Sources.Paths` | `Namotion.Interceptor.Registry.Paths` |
| `ISourcePathProvider` (6 methods) | `IPathProvider` (3 methods) |
| `SourcePathProviderBase` | `PathProviderBase` |
| `SourcePathAttribute` | `PathAttribute` |
| `HomeBlaze.Abstractions.Attributes.ChildrenAttribute` | `Namotion.Interceptor.Registry.Attributes.ChildrenAttribute` |

---

## Benefits

1. **Simpler interface** - 3 methods instead of 6
2. **Segment-centric customization** - cleaner than path-level overrides
3. **Single source of truth** for path handling in Registry
4. **[Children] support** built into all path providers
5. **Type safety** through `ISubjectConnector` interface
6. **Full compatibility** with PR #114 transaction system
