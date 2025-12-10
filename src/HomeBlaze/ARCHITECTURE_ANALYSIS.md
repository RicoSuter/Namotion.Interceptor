# HomeBlaze Architecture Analysis

This document analyzes the current HomeBlaze architecture, identifies issues, and proposes improvements.

---

## Summary of Decisions

### Interface Renames

| Current | New | Notes |
|---------|-----|-------|
| `ITitleProvider` + `IIconProvider` | `IDisplayable` | Merged |
| `IStorageItem` | `IStorageFile` | Name, FullPath, Storage, ReadAsync, WriteAsync |
| `IPersistentSubject` | `IConfigurableSubject` | Method: `ApplyConfigurationAsync()` |
| `ISubjectStorageHandler` | `IConfigurationWriter` | Method: `WriteConfigurationAsync()` |

### New Interfaces

```csharp
public interface IDisplayable
{
    string? Title { get; }
    string? Icon { get; }
}

public interface IStorageContainer
{
    StorageStatus Status { get; }
    Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default);
    Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteBlobAsync(string path, CancellationToken ct = default);
}

public interface IStorageFile
{
    string Name { get; }
    string FullPath { get; }
    IStorageContainer Storage { get; }
    Task<Stream> ReadAsync(CancellationToken ct = default);
    Task WriteAsync(Stream content, CancellationToken ct = default);
}

public interface IConfigurableSubject
{
    Task ApplyConfigurationAsync(CancellationToken ct = default);
}

public interface IConfigurationWriter
{
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

### Class Renames

| Current | New | Notes |
|---------|-----|-------|
| `SubjectSerializer` | `ConfigurableSubjectSerializer` | Require IServiceProvider |
| `SubjectFactory` | `FileSubjectFactory` | File creation only |

### New Classes

| Class | Location | Purpose |
|-------|----------|---------|
| `TypeProvider` | Core | Central type source, lazy scanning |
| `StateUnitExtensions` | Core | Formatting extracted from StateAttribute |
| `StorageScanner` | Storage | Scan + build hierarchy |
| `StorageFileWatcher` | Storage | File system watching |
| `StoragePathRegistry` | Storage | Subject ↔ path mapping |

### Removed

- `ITitleProvider` (merged into IDisplayable)
- `IIconProvider` (merged into IDisplayable)
- `ReflectionHelper` (no longer needed)
- `ContentHashUtility` (inlined into StoragePathRegistry)
- `SubjectHierarchyManager` (merged into StorageScanner)

### Key Design Decisions

1. **TypeProvider + Lazy Registries**: Registries inject TypeProvider, scan lazily on first access
2. **VirtualFolder**: NOT IStorageContainer, just IDisplayable with Children
3. **IStorageFile.Storage**: Always points to root FluentStorageContainer (real storage)
4. **File constructors**: Standardize on `(IStorageContainer storage, string fullPath)`
5. **File content**: No Content property, use ReadAsync/WriteAsync on demand
6. **Change notification**: Components observe `LastModified` property changes

---

## 1. Current Architecture Overview

### Project Structure
```
HomeBlaze.Abstractions/    - Interfaces, attributes, contracts
HomeBlaze.Core/            - Core services (registries, serialization, persistence)
HomeBlaze.Storage/         - FluentStorage integration, file types
HomeBlaze/                 - Blazor UI components
```

### Key Concepts
- **Subject**: Any object implementing `IInterceptorSubject` (from Namotion.Interceptor)
- **Storage**: FluentStorage-based abstraction for file/blob persistence
- **Registry**: Discovery and lookup of subjects, components, and types

---

## 2. Interface Analysis

### 2.1 Display Interfaces (ITitleProvider, IIconProvider)

**DECIDED: Merge into IDisplayable**

```csharp
public interface IDisplayable
{
    string? Title { get; }
    string? Icon { get; }
}
```

Replaces: `ITitleProvider` + `IIconProvider`

---

### 2.2 Storage Interfaces (IStorageItem, IStorageContainer)

**DECIDED: Redesign storage interfaces**

```csharp
public interface IStorageContainer
{
    StorageStatus Status { get; }

    Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default);
    Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteBlobAsync(string path, CancellationToken ct = default);
}

public interface IStorageItem
{
    string Name { get; }               // "readme.md"
    string FullPath { get; }           // "docs/readme.md" (relative to container)
    IStorageContainer Storage { get; } // parent container
}
```

**Implementers:**
- `IStorageContainer`: FluentStorageContainer
- `IStorageItem`: GenericFile, MarkdownFile, JsonFile, VirtualFolder

Note: `StorageType` and `ConnectionString` remain as implementation details on `FluentStorageContainer` with `[Configuration]` attributes

---

### 2.3 Storage and Persistence Interfaces

**DECIDED: Separate blob operations from configuration persistence**

Two distinct concerns:

**1. IStorageBlob - for file content:**
```csharp
public interface IStorageBlob
{
    string Name { get; }
    string FullPath { get; }
    IStorageContainer Storage { get; }

    Task<Stream> ReadAsync(CancellationToken ct = default);
    Task WriteAsync(Stream content, CancellationToken ct = default);
}
```

**2. IConfigurableSubject - for configuration:**
```csharp
public interface IConfigurableSubject
{
    Task ApplyConfigurationAsync(CancellationToken ct = default);
}

public interface IConfigurationWriter
{
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

**Change notification pattern:**
- File subjects have `[State] public DateTime LastModified`
- Components set up targeted observable: filter for specific subject + `LastModified` property
- Avoids full page re-render (fixes flickering issue)
- Component calls `ReadAsync()` when `LastModified` changes

**File class changes:**
- Remove `Content` property from MarkdownFile/JsonFile (read on demand)
- Keep metadata: `Name`, `FullPath`, `FileSize`, `LastModified`
- MarkdownFile keeps cached `Title` (extracted when LastModified changes)

**Implementers:**
- `IStorageBlob`: GenericFile, MarkdownFile, JsonFile
- `IConfigurableSubject`: Any subject with `[Configuration]` properties
- `IConfigurationWriter`: RootManager, FluentStorageContainer

**Renames:**
- `IPersistentSubject` → `IConfigurableSubject`
- `IPersistentSubject.ReloadAsync` → `IConfigurableSubject.ApplyConfigurationAsync`
- `ISubjectStorageHandler` → `IConfigurationWriter`
- `ISubjectStorageHandler.WriteAsync` → `IConfigurationWriter.WriteConfigurationAsync`

---

## 3. Registry Analysis

### 3.1 TypeProvider + Lazy Registries

**DECIDED: Introduce TypeProvider with lazy scanning**

**TypeProvider** - central type source:
```csharp
public class TypeProvider
{
    private readonly List<Type> _types = new();

    public IReadOnlyCollection<Type> Types => _types;

    public void AddAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                _types.AddRange(assembly.GetExportedTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                _types.AddRange(exception.Types.Where(type => type != null)!);
            }
        }
    }

    // Future: Add types from plugin loader
    public void AddTypes(IEnumerable<Type> types)
    {
        _types.AddRange(types);
    }
}
```

**Registries** - constructor injection + lazy scanning:
```csharp
public class SubjectTypeRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly Lazy<ConcurrentDictionary<string, Type>> _typesByName;
    private readonly Lazy<ConcurrentDictionary<string, Type>> _typesByExtension;

    public SubjectTypeRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
        _typesByName = new Lazy<ConcurrentDictionary<string, Type>>(ScanTypes);
        _typesByExtension = new Lazy<ConcurrentDictionary<string, Type>>(ScanExtensions);
    }

    public Type? ResolveType(string typeName)
    {
        _typesByName.Value.TryGetValue(typeName, out var type);
        return type;
    }

    private ConcurrentDictionary<string, Type> ScanTypes() { /* scan _typeProvider.Types */ }
}
```

**Key changes:**
- No manual `Register<T>()` methods - all discovery via attributes
- No base class needed - registries just use TypeProvider
- Lazy scanning on first access
- Ready for future plugin integration (copy `Namotion.NuGetPlugins` from V1)

---

## 4. Storage Handler Analysis

### 4.1 ISubjectStorageHandler Implementations

**Current State:**

| Handler | Owns | Purpose |
|---------|------|---------|
| `RootManager` | Root subject only | Saves to `root.json` |
| `FluentStorageContainer` | Its children | Saves to storage paths |

**Issue:** Both implement `ISubjectStorageHandler` but with different scopes:
- `RootManager` checks if subject == Root
- `FluentStorageContainer` looks up path from `_pathTracker`

**Observation:** This is actually a good pattern - chain of responsibility for storage.

**Potential Improvement:** Make the pattern more explicit:
```csharp
public interface ISubjectStorageHandler
{
    bool CanHandle(IInterceptorSubject subject);
    Task WriteAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

---

### 4.2 StorageService Registration

**Current State:** Subjects must be manually registered with `StorageService.RegisterSubject()`.

**Issue:** Easy to forget, not automatic.

**Proposal:** Consider automatic registration via:
1. Subject attach lifecycle hooks
2. Storage handler scanning at startup
3. Keep manual for explicit control (current approach)

---

## 5. Serializer Analysis

### 5.1 ConfigurableSubjectSerializer Location

**DECIDED: Keep in Core, require IServiceProvider**

Renamed `SubjectSerializer` → `ConfigurableSubjectSerializer`

```csharp
public class ConfigurableSubjectSerializer
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly IServiceProvider _serviceProvider;  // Now required

    public ConfigurableSubjectSerializer(
        SubjectTypeRegistry typeRegistry,
        IServiceProvider serviceProvider)
    {
        _typeRegistry = typeRegistry;
        _serviceProvider = serviceProvider;
    }

    private IInterceptorSubject? CreateInstance(Type type, IInterceptorSubjectContext context)
    {
        // Single code path - use DI only
        var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
        if (instance is IInterceptorSubject subject)
        {
            subject.Context.AddFallbackContext(context);
            return subject;
        }

        throw new InvalidOperationException(
            $"Type {type.FullName} must implement IInterceptorSubject.");
    }
}
```

**Changes:**
- `IServiceProvider` now required (was optional)
- Removed fallback constructor patterns (context-only, parameterless)
- Subjects use proper DI for dependencies

---

### 5.2 StateAttribute - Extract Formatting

**DECIDED: Move formatting to extension method in Core**

StateAttribute stays pure metadata:
```csharp
public class StateAttribute : Attribute
{
    public string? Name { get; set; }
    public StateUnit Unit { get; set; }
    public int Order { get; set; }
    public bool IsCumulative { get; set; }
    public bool IsSignal { get; set; }
    public bool IsEstimated { get; set; }
}
```

Formatting extracted to extension (HomeBlaze.Core):
```csharp
public static class StateUnitExtensions
{
    public static string FormatValue(this StateUnit unit, object? value)
    {
        if (value == null) return "";

        return unit switch
        {
            StateUnit.Percent => $"{(int)(Convert.ToDecimal(value) * 100m)}%",
            StateUnit.DegreeCelsius => $"{value} °C",
            StateUnit.Watt => $"{value} W",
            // ... etc
        };
    }
}
```

Usage: `stateAttribute.Unit.FormatValue(value)`

---

## 6. File Subject Analysis

### 6.1 File Subject Constructors

**DECIDED: Standardize constructors and split factory**

**Standardized constructor for all file subjects:**
```csharp
public GenericFile(IStorageContainer storage, string fullPath)
{
    Storage = storage;
    FullPath = fullPath;
    Name = Path.GetFileName(fullPath);
}
```

**Split SubjectFactory into:**

1. **`FileSubjectFactory`** (HomeBlaze.Storage):
```csharp
public class FileSubjectFactory
{
    public IStorageFile Create(Type type, IStorageContainer storage, string fullPath, Blob metadata);
    public async Task<IStorageFile> CreateFromBlobAsync(IStorageContainer storage, Blob blob, CancellationToken ct);
}
```

2. **Move `UpdateFromJsonAsync` to `ConfigurableSubjectSerializer`**

**Naming consistency:**
- `IStorageBlob` → `IStorageFile`
- `SubjectSerializer` → `ConfigurableSubjectSerializer`
- `SubjectFactory` → `FileSubjectFactory` (file creation only)

---

### 6.2 JsonFile vs Deserialized JSON Subjects

**DECIDED: Keep dual pattern**

| JSON File | Behavior |
|-----------|----------|
| Has `"Type": "..."` discriminator | Deserialized to typed subject (e.g., `Motor`) |
| Plain JSON (no Type) | Wrapped in `JsonFile` implementing `IStorageBlob` |

- `JsonFile` has no `Content` property - uses `ReadAsync()` / `WriteAsync()` on demand
- Future option: Could use `$schema` URL as alternative marker for typed subjects

**Previous analysis (kept for reference):**
- JSON with valid `Type` discriminator → Deserialized to typed subject
- JSON without `Type` or unknown type → Wrapped in `JsonFile`

**Observation:** This seems intentional and useful. Document as a feature.

---

### 6.3 VirtualFolder - Hierarchical Grouping Only

**DECIDED: VirtualFolder does NOT implement IStorageContainer**

```csharp
public partial class VirtualFolder : IDisplayable
{
    public string RelativePath { get; }

    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public string? Title => Path.GetFileName(RelativePath.TrimEnd('/'));
    public string Icon => FolderIcon;
}
```

**Reasoning:**
- `IStorageBlob.Storage` should always return the actual storage that can read/write
- Folders are virtual groupings, not real storage backends
- Avoids need to traverse up to find real storage

**IStorageBlob always references root:**
```csharp
public interface IStorageBlob
{
    string Name { get; }                    // "readme.md"
    string FullPath { get; }                // "docs/subfolder/readme.md"
    IStorageContainer Storage { get; }      // FluentStorageContainer (root)
}

// Usage - direct access to real storage
await file.Storage.ReadBlobAsync(file.FullPath, ct);
```

**IStorageContainer implementers:**
- `FluentStorageContainer` only (real storage backends)

---

## 7. Storage Internal Classes Cleanup

**DECIDED: Reorganize into 5 focused classes (~100-150 loc each)**

| Class | Responsibility | Lines |
|-------|----------------|-------|
| `FluentStorageContainer` | Public IStorageContainer, coordinates others, config properties | ~150 |
| `StorageScanner` | Scan storage, build VirtualFolder hierarchy, create subjects | ~150 |
| `StorageFileWatcher` | FileSystemWatcher wrapper, debounce, track own writes | ~150 |
| `StoragePathRegistry` | Map subjects ↔ paths, track hashes for change detection | ~100 |
| `FileSubjectFactory` | Create file subjects with standardized constructor | ~100 |

**Absorb/Remove:**
- `SubjectHierarchyManager` → merged into `StorageScanner`
- `StoragePathTracker` → renamed to `StoragePathRegistry`
- `ContentHashUtility` → inlined into `StoragePathRegistry`
- `FileSystemWatcherService` → renamed to `StorageFileWatcher`
- `ReflectionHelper` → removed (no longer needed)
- `SubjectFactory` → split, file parts to `FileSubjectFactory`

---

## 8. Previous Internal Classes Analysis (Archived)

### 7.1 SubjectFactory Responsibilities

**Current State:** Located in `HomeBlaze.Storage.Internal`

**Responsibilities:**
1. Create subjects from blobs based on file type
2. Handle JSON deserialization (delegates to SubjectSerializer)
3. Handle constructor reflection for file subjects
4. Update subject properties from JSON
5. Serialize subjects (delegates to SubjectSerializer)

**Issue:** Too many responsibilities, tight coupling to FluentStorageContainer.

**Proposal:** Split into:
1. `FileSubjectFactory` - Creates file subjects (GenericFile, MarkdownFile, etc.)
2. Keep `SubjectSerializer` for JSON serialization
3. Move `UpdateFromJsonAsync` logic to serializer

---

### 7.2 ReflectionHelper Usage

**Current State:** `SubjectFactory.CreateFileSubject` uses reflection to:
- Find appropriate constructor
- Set properties like `Storage`, `FilePath`, `FileName`

**Issue:** Fragile, depends on property names.

**Proposal:** Use interface instead:
```csharp
public interface IStorageAware
{
    void InitializeStorage(FluentStorageContainer storage, string path);
}
```

---

## 8. Naming Suggestions Summary

| Current Name | Proposed Name | Reason |
|--------------|---------------|--------|
| `ITitleProvider` + `IIconProvider` | `IDisplayInfo` | Consolidation |
| `IStorageItem.FileName` | `IPathItem.Name` | More generic |
| `IStorageItem.FilePath` | `IPathItem.Path` | More generic |
| `StateAttribute` | `DisplayAttribute` | Clearer purpose |
| `ConfigurationAttribute` | `PersistedAttribute` | Clearer purpose |
| `SubjectFactory` | `FileSubjectFactory` | Narrower scope |
| `IPersistentSubject` | Keep or `IReloadable` | Clear purpose |

---

## 9. Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         HomeBlaze.Abstractions                       │
├─────────────────────────────────────────────────────────────────────┤
│  Interfaces:                    Attributes:                         │
│  - ITitleProvider               - [State]                          │
│  - IIconProvider                - [Configuration]                  │
│  - IStorageItem                 - [FileExtension]                  │
│  - IPersistentSubject           - [SubjectComponent]               │
│  - ISubjectStorageHandler                                          │
│  - ISubjectComponent                                               │
└───────────────────────────────────┬─────────────────────────────────┘
                                    │
┌───────────────────────────────────┴─────────────────────────────────┐
│                           HomeBlaze.Core                            │
├─────────────────────────────────────────────────────────────────────┤
│  Services:                                                          │
│  - SubjectTypeRegistry     (type resolution)                       │
│  - SubjectComponentRegistry (UI component lookup)                  │
│  - SubjectSerializer       (JSON serialization)                    │
│  - StorageService          (auto-persistence)                      │
│  - RootManager             (root.json handling)                    │
│                                                                     │
│  Extensions:                                                        │
│  - SubjectRegistryExtensions (registry helpers)                    │
└───────────────────────────────────┬─────────────────────────────────┘
                                    │
┌───────────────────────────────────┴─────────────────────────────────┐
│                          HomeBlaze.Storage                          │
├─────────────────────────────────────────────────────────────────────┤
│  Storage:                       Files:                              │
│  - FluentStorageContainer       - GenericFile                      │
│  - VirtualFolder                - MarkdownFile                     │
│                                 - JsonFile                         │
│  Internal:                                                          │
│  - SubjectFactory                                                  │
│  - SubjectHierarchyManager                                         │
│  - StoragePathTracker                                              │
│  - FileSystemWatcherService                                        │
└───────────────────────────────────┬─────────────────────────────────┘
                                    │
┌───────────────────────────────────┴─────────────────────────────────┐
│                            HomeBlaze (UI)                           │
├─────────────────────────────────────────────────────────────────────┤
│  Components:                                                        │
│  - SubjectBrowser                                                  │
│  - SubjectPropertyPanel                                            │
│  - SubjectConfigurationDialog                                      │
│  - HomeBlazorComponentBase                                         │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 10. Priority Action Items

### High Priority
1. **Document the JsonFile vs Deserialized pattern** - This is intentional, needs docs
2. **Standardize file subject constructors** - Reduce reflection complexity
3. **Consider IStorageAware interface** - Replace reflection-based property setting

### Medium Priority
4. **Evaluate ITitleProvider/IIconProvider merge** - Discuss trade-offs
5. **Add ISelfPersistent interface** - Clarify save responsibility
6. **Extract ISubjectSerializer interface** - Better abstraction

### Low Priority
7. **Rename StateAttribute to DisplayAttribute** - Breaking change
8. **Rename ConfigurationAttribute to PersistedAttribute** - Breaking change
9. **Create registry base class** - Nice-to-have

---

## 11. Questions for Discussion

1. Should `ITitleProvider` and `IIconProvider` be merged?
2. Is the current `IPersistentSubject` / `ISubjectStorageHandler` split correct?
3. Should `VirtualFolder` have operation methods or be data-only?
4. Are the attribute names (`[State]`, `[Configuration]`) clear enough?
5. Should `SubjectSerializer` be moved to Abstractions?
6. How should we handle the file subject constructor inconsistency?

---

## 12. Next Steps

1. Review this document together
2. Decide on each proposal
3. Create implementation plan for approved changes
4. Implement in phases to minimize breaking changes
