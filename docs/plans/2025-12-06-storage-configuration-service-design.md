# Storage & Configuration Service Design

## Overview

This design introduces a unified storage abstraction using FluentStorage and automatic configuration persistence via a background service.

## Core Components

### 1. ConfigurableAttribute

Marks a subject class as JSON-serializable with `[Configuration]` properties.

```csharp
// HomeBlaze.Abstractions/Attributes/ConfigurableAttribute.cs
[AttributeUsage(AttributeTargets.Class)]
public class ConfigurableAttribute : Attribute { }
```

### 2. ISubjectStorageHandler

Interface for components that can persist subject configurations to storage.

```csharp
// HomeBlaze.Abstractions/Storage/ISubjectStorageHandler.cs
public interface ISubjectStorageHandler
{
    /// <returns>true if saved, false if not this handler's subject</returns>
    /// <exception cref="IOException">On transient errors (retry later)</exception>
    Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

### 3. StorageService

Background service that listens to property changes and auto-saves configurations.

```csharp
// HomeBlaze.Core/Services/StorageService.cs
public class StorageService : BackgroundService
{
    // Copy-on-write pattern for lock-free iteration (same as PropertyChangeQueue)
    private volatile ISubjectStorageHandler[] _handlers = [];
    private readonly Lock _handlersLock = new();
    private readonly ConcurrentQueue<(IInterceptorSubject, int retryCount)> _retryQueue = new();

    public void RegisterHandler(ISubjectStorageHandler handler)
    {
        lock (_handlersLock)
        {
            var handlers = _handlers;
            var updated = new ISubjectStorageHandler[handlers.Length + 1];
            Array.Copy(handlers, updated, handlers.Length);
            updated[handlers.Length] = handler;
            _handlers = updated;
        }
    }

    public void UnregisterHandler(ISubjectStorageHandler handler)
    {
        lock (_handlersLock)
        {
            _handlers = _handlers.Where(h => h != handler).ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Uses PropertyChangeQueue (not Observable) for high performance
        var subscription = _context.CreatePropertyChangeQueueSubscription();

        while (!ct.IsCancellationRequested)
        {
            // Process property changes
            // Filter for [Configuration] attributes (cache lookup per type)
            // Iterate _handlers (volatile read, lock-free) until one returns true
            // On IOException: queue for retry with exponential backoff
        }
    }
}
```

### 4. Storage

Root of a storage context using FluentStorage. Implements `ISubjectStorageHandler`.
Note: Does NOT extend BackgroundService - StorageService manages all background work.

```csharp
// HomeBlaze.Storage/Storage.cs
[InterceptorSubject, Configurable]
public partial class Storage : ISubjectStorageHandler, ITitleProvider, IDisposable
{
    private IBlobStorage? _client;
    // Thread-safe: accessed from StorageService background thread and UI thread
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();

    // Configuration
    [Configuration] public partial string StorageType { get; set; }      // "filesystem", "azure-blob"
    [Configuration] public partial string ConnectionString { get; set; } // path or connection string
    [Configuration] public partial string? ContainerName { get; set; }   // for cloud storage

    [State] public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
    [State] public partial StorageStatus Status { get; set; }

    // ISubjectStorageHandler - called by StorageService background thread
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (!_subjectPaths.TryGetValue(subject, out var path)) return false;
        var json = _serializer.Serialize(subject);
        await _client!.WriteTextAsync(path, json, ct);
        return true;
    }

    // Public API for content management
    public Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken ct);
    public Task DeleteSubjectAsync(string path, CancellationToken ct);
    public Task WriteBlobAsync(string path, Stream content, CancellationToken ct);  // Upsert
    public Task DeleteBlobAsync(string path, CancellationToken ct);
    public Task<Stream> ReadBlobAsync(string path, CancellationToken ct);

    // Scanning - called on initialization or refresh
    public Task ScanAsync(CancellationToken ct);
}
```

### 5. VirtualFolder

Hierarchical grouping that delegates operations to parent Storage.

```csharp
// HomeBlaze.Storage/VirtualFolder.cs
[InterceptorSubject]
public partial class VirtualFolder : ITitleProvider
{
    public Storage Storage { get; }
    public string RelativePath { get; }  // "folder/subfolder/"

    [State] public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    // Delegates to Storage with path prefix
    public Task AddSubjectAsync(string name, IInterceptorSubject subject, CancellationToken ct)
        => Storage.AddSubjectAsync(RelativePath + name, subject, ct);

    public Task DeleteSubjectAsync(string name, CancellationToken ct)
        => Storage.DeleteSubjectAsync(RelativePath + name, ct);

    public Task WriteBlobAsync(string name, Stream content, CancellationToken ct)
        => Storage.WriteBlobAsync(RelativePath + name, content, ct);

    public Task DeleteBlobAsync(string name, CancellationToken ct)
        => Storage.DeleteBlobAsync(RelativePath + name, ct);
}
```

### 6. FileSubjectFactory

Creates file subjects based on extension registry.

```csharp
// HomeBlaze.Storage/FileSubjectFactory.cs
public class FileSubjectFactory
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;

    public async Task<IInterceptorSubject?> CreateFromBlobAsync(
        Blob blob,
        Storage storage,
        IInterceptorSubjectContext context,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();

        // JSON files - check if [Configurable] type
        if (extension == ".json")
        {
            var json = await blob.ReadTextAsync(ct);
            var typeDiscriminator = ExtractTypeFromJson(json);

            if (typeDiscriminator != null)
            {
                var type = _typeRegistry.ResolveType(typeDiscriminator);
                if (type?.GetCustomAttribute<ConfigurableAttribute>() != null)
                {
                    return _serializer.Deserialize(json, context);
                }
            }

            // Not configurable - create JsonFile
            return new JsonFile(context, storage, blob.FullPath) { Content = json };
        }

        // Other extensions - lookup in registry
        var fileType = _typeRegistry.ResolveTypeForExtension(extension) ?? typeof(GenericFile);
        return CreateFileSubject(fileType, storage, blob, context);
    }
}
```

### 7. File Subject Types

```csharp
// HomeBlaze.Storage/Files/MarkdownFile.cs
[InterceptorSubject]
public partial class MarkdownFile : ITitleProvider
{
    public Storage Storage { get; }
    public string BlobPath { get; }

    [State]
    public partial string Content { get; set; }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Content));
        await Storage.WriteBlobAsync(BlobPath, stream, ct);
    }
}

// HomeBlaze.Storage/Files/JsonFile.cs
[InterceptorSubject]
public partial class JsonFile : ITitleProvider
{
    public Storage Storage { get; }
    public string BlobPath { get; }

    [State]
    public partial string Content { get; set; }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Content));
        await Storage.WriteBlobAsync(BlobPath, stream, ct);
    }
}

// HomeBlaze.Storage/Files/GenericFile.cs
[InterceptorSubject]
public partial class GenericFile
{
    public Storage Storage { get; }
    public string BlobPath { get; }

    // Stream-based API for binary content
    public Task<Stream> OpenReadAsync(CancellationToken ct = default)
        => Storage.ReadBlobAsync(BlobPath, ct);

    public Task SaveAsync(Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(BlobPath, content, ct);
}
```

## Hierarchy Building

When Storage scans, it builds nested VirtualFolders:

```csharp
private void PlaceInHierarchy(string path, IInterceptorSubject subject)
{
    // path = "folder1/folder2/file.json"
    var segments = path.Split('/');
    var currentContainer = (IInterceptorSubject)this;

    for (int i = 0; i < segments.Length - 1; i++)
    {
        var folderName = segments[i];
        var children = GetChildren(currentContainer);

        if (!children.TryGetValue(folderName, out var existing))
        {
            var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
            var folder = new VirtualFolder(_context, this, relativePath);
            SetChildren(currentContainer, folderName, folder);
            currentContainer = folder;
        }
        else
        {
            currentContainer = (VirtualFolder)existing;
        }
    }

    SetChildren(currentContainer, segments[^1], subject);
}
```

**Result:**
```
Storage
└── Children["docs"] = VirtualFolder(RelativePath="docs/")
    └── Children["notes"] = VirtualFolder(RelativePath="docs/notes/")
        └── Children["file.json"] = Subject
```

## RootManager Integration

RootManager also implements `ISubjectStorageHandler` for root.json:

```csharp
public class RootManager : ISubjectStorageHandler, IDisposable
{
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (subject != Root) return false;
        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(_configPath!, json, ct);
        return true;
    }
}
```

## Key Design Decisions

1. **`[Configurable]`** attribute marks subjects as JSON-serializable
2. **Stream-based blob API** - `WriteBlobAsync(path, Stream)` for memory efficiency
3. **Upsert semantics** - `WriteBlobAsync` creates or updates (no separate Add/Update)
4. **Centralized path tracking** - Storage tracks all `_subjectPaths`, VirtualFolders delegate
5. **PropertyChangeQueue** - StorageService uses high-perf queue, not Observable
6. **Retry with backoff** - IOException triggers retry queue with exponential backoff
7. **FileSubjectFactory** - Uses SubjectTypeRegistry for extensible file type mapping
8. **No IFileSubject interface** - File types have Storage/BlobPath/SaveAsync directly (simpler)

## Files to Create/Modify

### New Files
- `HomeBlaze.Abstractions/Attributes/ConfigurableAttribute.cs`
- `HomeBlaze.Abstractions/Storage/ISubjectStorageHandler.cs`
- `HomeBlaze.Core/Services/StorageService.cs`
- `HomeBlaze.Storage/Storage.cs`
- `HomeBlaze.Storage/VirtualFolder.cs`
- `HomeBlaze.Storage/FileSubjectFactory.cs`
- `HomeBlaze.Storage/Files/JsonFile.cs`

### Modified Files
- `HomeBlaze.Core/Services/RootManager.cs` - Implement ISubjectStorageHandler
- `HomeBlaze.Storage/Files/MarkdownFile.cs` - Add Storage ref, BlobPath, SaveAsync
- `HomeBlaze.Storage/Files/GenericFile.cs` - Add Storage ref, BlobPath, stream-based API

### Removed Files
- `HomeBlaze.Storage/StorageContainer.cs` - Replaced by Storage
- `HomeBlaze.Storage/FileSystemStorage.cs` - Replaced by Storage with FluentStorage
- `HomeBlaze.Storage/Folder.cs` - Replaced by VirtualFolder

## Dependencies

Add FluentStorage NuGet packages:
- `FluentStorage` - Core abstractions
- `FluentStorage.Azure.Blobs` - Azure Blob Storage (optional)
- `FluentStorage.AWS.S3` - AWS S3 (optional)

## Review Notes (2025-12-06)

Based on architecture, quality, and performance reviews:

### Applied Fixes
1. **Thread safety: `_handlers`** - Use copy-on-write volatile array pattern (like PropertyChangeQueue)
2. **Thread safety: `_subjectPaths`** - Use `ConcurrentDictionary`
3. **Storage lifecycle** - Removed `BackgroundService` inheritance; StorageService manages all background work
4. **Configuration attribute caching** - Note to cache `[Configuration]` property lookup per type

### Deferred Decisions
- **File type abstraction** - Keep simple for now (accept SaveAsync duplication); add base class later if needed
- **IStorageOperations interface** - Consider later for Storage/VirtualFolder unification
- **Bounded retry queue** - Implement if unbounded growth becomes an issue
- **Stream-based serialization** - Optimize later if memory pressure observed

### Open Questions (to resolve during implementation)
1. Handler priority: First-registered-first-tried (current) or add priority mechanism?
2. RootManager: Keep Rx approach or migrate to StorageService pattern?
3. Configuration changes on Storage: How to handle `_client` recreation when ConnectionString changes?
4. Debouncing: Per-subject or global debounce in StorageService?
