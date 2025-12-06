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

### 2. ISubjectConfigurationWriter

Interface for components that can persist subject configurations.

```csharp
// HomeBlaze.Abstractions/Storage/ISubjectConfigurationWriter.cs
public interface ISubjectConfigurationWriter
{
    /// <returns>true if saved, false if not this writer's subject</returns>
    /// <exception cref="IOException">On transient errors (retry later)</exception>
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

### 3. IFileSubject

Interface for file-backed subjects with save capability.

```csharp
// HomeBlaze.Abstractions/Storage/IFileSubject.cs
public interface IFileSubject
{
    Storage Storage { get; }
    string BlobPath { get; }
}
```

### 4. ConfigurationService

Background service that listens to property changes and auto-saves configurations.

```csharp
// HomeBlaze.Core/Services/ConfigurationService.cs
public class ConfigurationService : BackgroundService
{
    private readonly List<ISubjectConfigurationWriter> _writers = new();
    private readonly ConcurrentQueue<(IInterceptorSubject, int retryCount)> _retryQueue = new();

    public void RegisterWriter(ISubjectConfigurationWriter writer);
    public void UnregisterWriter(ISubjectConfigurationWriter writer);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Uses PropertyChangeQueue (not Observable) for high performance
        var subscription = _context.CreatePropertyChangeQueueSubscription();

        while (!ct.IsCancellationRequested)
        {
            // Process property changes
            // Filter for [Configuration] attributes
            // Iterate writers until one returns true
            // On IOException: queue for retry with exponential backoff
        }
    }
}
```

### 5. Storage

Root of a storage context using FluentStorage. Implements `ISubjectConfigurationWriter`.

```csharp
// HomeBlaze.Storage/Storage.cs
[InterceptorSubject, Configurable]
public partial class Storage : BackgroundService, ISubjectConfigurationWriter, ITitleProvider
{
    private IBlobStorage? _client;
    private readonly Dictionary<IInterceptorSubject, string> _subjectPaths = new();

    // Configuration
    [Configuration] public partial string StorageType { get; set; }      // "filesystem", "azure-blob"
    [Configuration] public partial string ConnectionString { get; set; } // path or connection string
    [Configuration] public partial string? ContainerName { get; set; }   // for cloud storage

    [State] public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
    [State] public partial StorageStatus Status { get; set; }

    // ISubjectConfigurationWriter - called by ConfigurationService
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct)
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
}
```

### 6. VirtualFolder

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

### 7. FileSubjectFactory

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

### 8. File Subject Types

```csharp
// HomeBlaze.Storage/Files/MarkdownFile.cs
[InterceptorSubject]
public partial class MarkdownFile : IFileSubject, ITitleProvider
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
public partial class JsonFile : IFileSubject, ITitleProvider
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
public partial class GenericFile : IFileSubject
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

RootManager also implements `ISubjectConfigurationWriter` for root.json:

```csharp
public class RootManager : ISubjectConfigurationWriter, IDisposable
{
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct)
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
5. **PropertyChangeQueue** - ConfigurationService uses high-perf queue, not Observable
6. **Retry with backoff** - IOException triggers retry queue with exponential backoff
7. **FileSubjectFactory** - Uses SubjectTypeRegistry for extensible file type mapping
8. **IFileSubject.SaveAsync()** - Text files save via property, GenericFile takes Stream param

## Files to Create/Modify

### New Files
- `HomeBlaze.Abstractions/Attributes/ConfigurableAttribute.cs`
- `HomeBlaze.Abstractions/Storage/ISubjectConfigurationWriter.cs`
- `HomeBlaze.Abstractions/Storage/IFileSubject.cs`
- `HomeBlaze.Core/Services/ConfigurationService.cs`
- `HomeBlaze.Storage/Storage.cs`
- `HomeBlaze.Storage/VirtualFolder.cs`
- `HomeBlaze.Storage/FileSubjectFactory.cs`
- `HomeBlaze.Storage/Files/JsonFile.cs`

### Modified Files
- `HomeBlaze.Core/Services/RootManager.cs` - Implement ISubjectConfigurationWriter
- `HomeBlaze.Storage/Files/MarkdownFile.cs` - Add IFileSubject, Storage ref, SaveAsync
- `HomeBlaze.Storage/Files/GenericFile.cs` - Add IFileSubject, stream-based API

### Removed Files
- `HomeBlaze.Storage/StorageContainer.cs` - Replaced by Storage
- `HomeBlaze.Storage/FileSystemStorage.cs` - Replaced by Storage with FluentStorage
- `HomeBlaze.Storage/Folder.cs` - Replaced by VirtualFolder

## Dependencies

Add FluentStorage NuGet packages:
- `FluentStorage` - Core abstractions
- `FluentStorage.Azure.Blobs` - Azure Blob Storage (optional)
- `FluentStorage.AWS.S3` - AWS S3 (optional)
