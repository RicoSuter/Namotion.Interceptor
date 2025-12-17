# Unified Connectors Architecture - Summary

## Design Goals

1. **Simplify** the path provider interface from 6 methods to 3
2. **Unify** path handling in `Namotion.Interceptor.Registry`
3. **Rename** Sources → Connectors for clarity (used by both sources and servers)
4. **Add** `[Children]` attribute support for implicit child path resolution

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    Namotion.Interceptor.Registry                │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ Paths/                                                   │   │
│  │  ├── IPathProvider (3 methods)                          │   │
│  │  ├── PathProviderBase (separators, [Children] support)  │   │
│  │  ├── DefaultPathProvider                                │   │
│  │  ├── JsonCamelCasePathProvider                          │   │
│  │  ├── AttributeBasedPathProvider                         │   │
│  │  ├── PathExtensions                                     │   │
│  │  └── ChildrenAttributeCache                             │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │ Attributes/                                              │   │
│  │  ├── ChildrenAttribute                                  │   │
│  │  └── PathAttribute                                      │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                              ▲
                              │ depends on
┌─────────────────────────────────────────────────────────────────┐
│                 Namotion.Interceptor.Connectors                 │
│  ┌────────────────────────┐  ┌───────────────────────────────┐ │
│  │ ISubjectConnector      │  │ Updates/                      │ │
│  │  - RootSubject         │  │  ├── SubjectUpdate            │ │
│  │  - PathProvider        │  │  ├── SubjectPropertyUpdate    │ │
│  └───────────┬────────────┘  │  └── ISubjectUpdateProcessor  │ │
│              │               └───────────────────────────────┘ │
│  ┌───────────▼────────────┐  ┌───────────────────────────────┐ │
│  │ Sources/               │  │ Other                         │ │
│  │  ├── ISubjectSource    │  │  ├── ISubjectFactory          │ │
│  │  ├── BackgroundService │  │  ├── ChangeQueueProcessor     │ │
│  │  ├── PropertyWriter    │  │  └── Resilience/CircuitBreaker│ │
│  │  └── Transactions/     │  └───────────────────────────────┘ │
│  └────────────────────────┘                                    │
└─────────────────────────────────────────────────────────────────┘
                              ▲
                              │ implements
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
│  MQTT Package    │  │  OPC UA Package  │  │  ASP.NET Core    │
│  - ClientSource  │  │  - ClientSource  │  │  - Controllers   │
│  - ServerService │  │  - ServerService │  │  - SignalR Hubs  │
└──────────────────┘  └──────────────────┘  └──────────────────┘
```

## Simplified IPathProvider

**Before (6 methods):**
```csharp
interface ISourcePathProvider {
    bool IsPropertyIncluded(property);
    string? TryGetPropertySegment(property);
    string GetPropertyFullPath(propertiesInPath);      // ← Removed
    IEnumerable<...> ParsePathSegments(path);          // ← Removed
    Property? TryGetPropertyFromSegment(subject, seg);
    Property? TryGetAttributeFromSegment(prop, seg);   // ← Removed
}
```

**After (3 methods):**
```csharp
interface IPathProvider {
    bool IsPropertyIncluded(property);
    string GetPropertySegment(property);
    Property? GetPropertyFromSegment(subject, segment);
}
```

**Key insight:** Customization happens at the segment level, not path level. Full path operations are extension methods.

## [Children] Attribute

Marks a dictionary property as implicit child container:

```csharp
[InterceptorSubject]
public partial class Storage
{
    [Children]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
}
```

**Path resolution with [Children]:**
- Path `files/readme.md` resolves to `Children["files"].Children["readme.md"]`
- Direct properties take precedence over child keys
- Built into `PathProviderBase.GetPropertyFromSegment`

## ISubjectConnector

Common interface for sources and servers:

```csharp
interface ISubjectConnector {
    IInterceptorSubject RootSubject { get; }
    IPathProvider PathProvider { get; }
}

interface ISubjectSource : ISubjectConnector {
    int WriteBatchSize { get; }
    Task<IDisposable?> StartListeningAsync(...);
    ValueTask WriteChangesAsync(...);
    Task<Action?> LoadInitialStateAsync(...);
}
```

Servers optionally implement `ISubjectConnector` for type consistency.

## Breaking Changes

| Before | After |
|--------|-------|
| `Namotion.Interceptor.Sources` | `Namotion.Interceptor.Connectors` |
| `Sources.Paths` | `Registry.Paths` |
| `ISourcePathProvider` | `IPathProvider` |
| `SourcePathAttribute` | `PathAttribute` |
