# Storage & Configuration Service Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement unified storage abstraction with FluentStorage and automatic configuration persistence via StorageService.

**Architecture:** StorageService (BackgroundService) listens to PropertyChangeQueue for `[Configuration]` property changes and delegates persistence to registered `ISubjectStorageHandler` implementations. Storage class uses FluentStorage for file operations, VirtualFolder provides hierarchical navigation.

**Tech Stack:** .NET 9, FluentStorage, System.Collections.Concurrent, Microsoft.Extensions.Hosting

**Design Document:** `docs/plans/2025-12-06-storage-configuration-service-design.md`

---

## Task 1: Add FluentStorage NuGet Package

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`

**Step 1: Add FluentStorage package reference**

Add to the `<ItemGroup>` in `HomeBlaze.Storage.csproj`:

```xml
<PackageReference Include="FluentStorage" Version="5.*" />
```

**Step 2: Verify package restores**

Run: `dotnet restore src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Success, no errors

**Step 3: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded

---

## Task 2: Create ConfigurableAttribute

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/ConfigurableAttribute.cs`

**Step 1: Create the attribute file**

```csharp
namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject class as JSON-serializable with [Configuration] properties.
/// Subjects with this attribute can be auto-persisted by StorageService.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class ConfigurableAttribute : Attribute
{
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeded

---

## Task 3: Create ISubjectStorageHandler Interface

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Storage/ISubjectStorageHandler.cs`

**Step 1: Create the Storage folder if needed**

Check if `src/HomeBlaze/HomeBlaze.Abstractions/Storage/` exists, create if not.

**Step 2: Create the interface file**

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Storage;

/// <summary>
/// Interface for components that can persist subject configurations to storage.
/// Implementations handle specific storage locations (e.g., root.json, Storage instances).
/// </summary>
public interface ISubjectStorageHandler
{
    /// <summary>
    /// Attempts to write the subject's configuration to storage.
    /// </summary>
    /// <param name="subject">The subject to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>true if this handler owns the subject and saved it; false if not this handler's subject.</returns>
    /// <exception cref="IOException">On transient I/O errors (retry later).</exception>
    Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct);
}
```

**Step 3: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeded

---

## Task 4: Create StorageService

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Core/Services/StorageService.cs`

**Step 1: Create the StorageService file**

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Background service that listens to property changes and auto-saves configurations.
/// Uses PropertyChangeQueue for high-performance change detection.
/// </summary>
public class StorageService : BackgroundService
{
    // Copy-on-write pattern for lock-free iteration (same as PropertyChangeQueue)
    private volatile ISubjectStorageHandler[] _handlers = [];
    private readonly Lock _handlersLock = new();

    private readonly ConcurrentQueue<(IInterceptorSubject Subject, int RetryCount)> _retryQueue = new();
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<StorageService>? _logger;

    // Cache for [Configuration] property lookup per type
    private static readonly ConcurrentDictionary<Type, HashSet<string>> _configurationPropertiesCache = new();

    private const int MaxRetries = 5;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public StorageService(
        IInterceptorSubjectContext context,
        ILogger<StorageService>? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Registers a storage handler to receive write requests.
    /// Thread-safe, uses copy-on-write pattern.
    /// </summary>
    public void RegisterHandler(ISubjectStorageHandler handler)
    {
        lock (_handlersLock)
        {
            var handlers = _handlers; // volatile read
            var updated = new ISubjectStorageHandler[handlers.Length + 1];
            Array.Copy(handlers, updated, handlers.Length);
            updated[handlers.Length] = handler;
            _handlers = updated; // volatile write
        }
        _logger?.LogInformation("Registered storage handler: {Type}", handler.GetType().Name);
    }

    /// <summary>
    /// Unregisters a storage handler.
    /// Thread-safe, uses copy-on-write pattern.
    /// </summary>
    public void UnregisterHandler(ISubjectStorageHandler handler)
    {
        lock (_handlersLock)
        {
            var handlers = _handlers; // volatile read
            var index = Array.IndexOf(handlers, handler);
            if (index >= 0)
            {
                var updated = new ISubjectStorageHandler[handlers.Length - 1];
                Array.Copy(handlers, 0, updated, 0, index);
                Array.Copy(handlers, index + 1, updated, index, handlers.Length - index - 1);
                _handlers = updated; // volatile write
            }
        }
        _logger?.LogInformation("Unregistered storage handler: {Type}", handler.GetType().Name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("StorageService starting");

        var queue = _context.TryGetService<PropertyChangeQueue>();
        if (queue == null)
        {
            _logger?.LogWarning("PropertyChangeQueue not found in context. StorageService will not auto-save.");
            return;
        }

        using var subscription = queue.Subscribe();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Process property changes with timeout to allow retry queue processing
                if (subscription.TryDequeue(out var change, TimeSpan.FromMilliseconds(100)))
                {
                    await ProcessChangeAsync(change, stoppingToken);
                }

                // Process retry queue
                await ProcessRetryQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in StorageService loop");
            }
        }

        _logger?.LogInformation("StorageService stopped");
    }

    private async Task ProcessChangeAsync(SubjectPropertyChange change, CancellationToken ct)
    {
        // Check if this is a [Configuration] property
        if (!IsConfigurationProperty(change.Property))
            return;

        var subject = change.Property.Subject;
        if (subject == null)
            return;

        _logger?.LogDebug("Configuration property changed: {Type}.{Property}",
            subject.GetType().Name, change.Property.Name);

        await WriteSubjectAsync(subject, retryCount: 0, ct);
    }

    private async Task WriteSubjectAsync(IInterceptorSubject subject, int retryCount, CancellationToken ct)
    {
        // Iterate handlers (volatile read, lock-free)
        var handlers = _handlers;

        foreach (var handler in handlers)
        {
            try
            {
                if (await handler.WriteAsync(subject, ct))
                {
                    _logger?.LogDebug("Saved subject via {Handler}: {Type}",
                        handler.GetType().Name, subject.GetType().Name);
                    return; // Successfully saved
                }
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Transient I/O error saving {Type}, will retry", subject.GetType().Name);

                if (retryCount < MaxRetries)
                {
                    _retryQueue.Enqueue((subject, retryCount + 1));
                }
                else
                {
                    _logger?.LogError("Max retries exceeded for {Type}", subject.GetType().Name);
                }
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving subject {Type}", subject.GetType().Name);
                return;
            }
        }

        // No handler claimed this subject - this is expected for subjects not tracked by any storage
    }

    private async Task ProcessRetryQueueAsync(CancellationToken ct)
    {
        if (_retryQueue.IsEmpty)
            return;

        // Process up to 10 retries per iteration
        for (int i = 0; i < 10 && _retryQueue.TryDequeue(out var item); i++)
        {
            var (subject, retryCount) = item;

            // Apply exponential backoff delay
            var delayIndex = Math.Min(retryCount - 1, RetryDelays.Length - 1);
            await Task.Delay(RetryDelays[delayIndex], ct);

            await WriteSubjectAsync(subject, retryCount, ct);
        }
    }

    private static bool IsConfigurationProperty(PropertyReference property)
    {
        var subjectType = property.Subject?.GetType();
        if (subjectType == null)
            return false;

        var configProperties = GetConfigurationProperties(subjectType);
        return configProperties.Contains(property.Name);
    }

    private static HashSet<string> GetConfigurationProperties(Type subjectType)
    {
        return _configurationPropertiesCache.GetOrAdd(subjectType, static type =>
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<ConfigurationAttribute>() != null)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
        });
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Core/HomeBlaze.Core.csproj`
Expected: Build succeeded

---

## Task 5: Update RootManager to Implement ISubjectStorageHandler

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Core/Services/RootManager.cs`

**Step 1: Add ISubjectStorageHandler interface implementation**

Add using statement at top:
```csharp
using HomeBlaze.Abstractions.Storage;
```

Change class declaration to:
```csharp
public class RootManager : ISubjectStorageHandler, IDisposable
```

**Step 2: Remove the Rx-based auto-save subscription**

Remove:
- The `_configurationChangeSubscription` field
- The `_saveDebounceDelay` field
- The `StartConfigurationChangeWatcher()` method
- The `IsConfigurationProperty()` method
- The subscription disposal in `Dispose()`
- The using statement for `System.Reactive.Concurrency` and `System.Reactive.Linq`
- Remove call to `StartConfigurationChangeWatcher()` in `LoadAsync`

**Step 3: Add WriteAsync method**

```csharp
/// <summary>
/// Writes the root subject configuration to disk if this is the root subject.
/// </summary>
public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
{
    if (subject != Root)
        return false;

    if (string.IsNullOrEmpty(_configPath))
        throw new InvalidOperationException("Cannot save: config path is not set");

    _logger?.LogInformation("Saving root configuration to: {Path}", _configPath);

    var json = _serializer.Serialize(Root);
    await File.WriteAllTextAsync(_configPath, json, ct);

    _logger?.LogInformation("Root configuration saved successfully");
    return true;
}
```

**Step 4: Update Dispose method**

```csharp
public void Dispose()
{
    // No subscriptions to dispose anymore - StorageService handles persistence
}
```

**Step 5: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Core/HomeBlaze.Core.csproj`
Expected: Build succeeded

---

## Task 6: Register StorageService and RootManager in DI

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs`

**Step 1: Read current Program.cs to understand DI setup**

Need to examine the current DI registration pattern.

**Step 2: Add StorageService registration**

Add after other service registrations:
```csharp
builder.Services.AddSingleton<StorageService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageService>());
```

**Step 3: Register RootManager with StorageService**

After RootManager is created and loaded, register it:
```csharp
var storageService = app.Services.GetRequiredService<StorageService>();
storageService.RegisterHandler(rootManager);
```

**Step 4: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`
Expected: Build succeeded

---

## Task 7: Run Tests

**Files:**
- Test: `src/HomeBlaze/HomeBlaze.Tests/`

**Step 1: Run all HomeBlaze tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Tests/HomeBlaze.Tests.csproj`
Expected: All tests pass

**Step 2: Fix any failures**

If tests fail, investigate and fix before proceeding.

---

## Task 8: Integration Test - Manual Verification

**Step 1: Start the application**

Run: `dotnet run --project src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`

**Step 2: Verify StorageService starts**

Expected: Log message "StorageService starting"

**Step 3: Test configuration change persistence**

1. Open UI
2. Change a [Configuration] property on a subject
3. Verify log shows "Saved subject via RootManager"
4. Stop and restart app
5. Verify change persisted

---

## Task 9: Commit Batch 1 (Foundation)

**Step 1: Stage and commit**

```bash
git add -A
git commit -m "feat: Add StorageService foundation with ISubjectStorageHandler

- Add ConfigurableAttribute for marking JSON-serializable subjects
- Add ISubjectStorageHandler interface for storage handlers
- Add StorageService BackgroundService with PropertyChangeQueue integration
- Update RootManager to implement ISubjectStorageHandler
- Remove Rx-based auto-save from RootManager (now handled by StorageService)
- Add FluentStorage NuGet package

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 10: Create Storage Class (Basic Structure)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Storage.cs`

**Step 1: Create the Storage class**

```csharp
using System.Collections.Concurrent;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage root using FluentStorage. Implements ISubjectStorageHandler.
/// </summary>
[InterceptorSubject, Configurable]
public partial class Storage : ISubjectStorageHandler, ITitleProvider, IDisposable
{
    // MudBlazor Icons.Material.Filled.Storage
    private const string StorageIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M2,20H22V16H2V20M4,17H6V19H4V17M2,4V8H22V4H2M6,7H4V5H6V7M2,14H22V10H2V14M4,11H6V13H4V11Z\" /></svg>";

    private IBlobStorage? _client;
    // Thread-safe: accessed from StorageService background thread and UI thread
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();

    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly ILogger<Storage>? _logger;

    /// <summary>
    /// Storage type identifier (e.g., "disk", "azure-blob").
    /// </summary>
    [Configuration]
    public partial string StorageType { get; set; }

    /// <summary>
    /// Connection string or path for the storage.
    /// For disk: absolute or relative path.
    /// For cloud: connection string.
    /// </summary>
    [Configuration]
    public partial string ConnectionString { get; set; }

    /// <summary>
    /// Container name (for cloud storage).
    /// </summary>
    [Configuration]
    public partial string? ContainerName { get; set; }

    /// <summary>
    /// Child subjects (files and folders).
    /// </summary>
    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    /// <summary>
    /// Current status of the storage.
    /// </summary>
    [State]
    public partial StorageStatus Status { get; set; }

    public string? Title => string.IsNullOrEmpty(ConnectionString)
        ? "Storage"
        : Path.GetFileName(ConnectionString.TrimEnd('/', '\\'));

    public string Icon => StorageIcon;

    public Storage(
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
        ILogger<Storage>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _logger = logger;

        StorageType = "disk";
        ConnectionString = string.Empty;
        Children = new Dictionary<string, IInterceptorSubject>();
        Status = StorageStatus.Disconnected;
    }

    /// <summary>
    /// Initializes the storage client based on configuration.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        Status = StorageStatus.Connecting;

        try
        {
            _client = StorageType.ToLowerInvariant() switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(
                    Path.GetFullPath(ConnectionString)),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };

            Status = StorageStatus.Connected;
            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType, ConnectionString);

            await ScanAsync(ct);
        }
        catch (Exception ex)
        {
            Status = StorageStatus.Error;
            _logger?.LogError(ex, "Failed to connect to storage");
            throw;
        }
    }

    /// <summary>
    /// Scans the storage and builds the subject hierarchy.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        _logger?.LogInformation("Scanning storage...");

        var blobs = await _client.ListAsync(recurse: true, cancellationToken: ct);

        // Clear existing children
        Children = new Dictionary<string, IInterceptorSubject>();
        _subjectPaths.Clear();

        foreach (var blob in blobs.Where(b => !b.IsFolder))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var subject = await CreateSubjectFromBlobAsync(blob, ct);
                if (subject != null)
                {
                    PlaceInHierarchy(blob.FullPath, subject);
                    _subjectPaths[subject] = blob.FullPath;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create subject for blob: {Path}", blob.FullPath);
            }
        }

        _logger?.LogInformation("Scan complete. Found {Count} subjects", _subjectPaths.Count);
    }

    /// <summary>
    /// ISubjectStorageHandler - called by StorageService background thread.
    /// </summary>
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (_client == null)
            return false;

        if (!_subjectPaths.TryGetValue(subject, out var path))
            return false;

        var json = _serializer.Serialize(subject);
        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        _logger?.LogDebug("Saved subject to storage: {Path}", path);
        return true;
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        var json = _serializer.Serialize(subject);
        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        _subjectPaths[subject] = path;
        PlaceInHierarchy(path, subject);

        _logger?.LogInformation("Added subject to storage: {Path}", path);
    }

    /// <summary>
    /// Deletes a subject from storage.
    /// </summary>
    public async Task DeleteSubjectAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        await _client.DeleteAsync(path, cancellationToken: ct);

        // Remove from tracking
        var subject = _subjectPaths.FirstOrDefault(kvp => kvp.Value == path).Key;
        if (subject != null)
        {
            _subjectPaths.TryRemove(subject, out _);
        }

        // Remove from hierarchy (simplified - full implementation would update Children)
        _logger?.LogInformation("Deleted from storage: {Path}", path);
    }

    /// <summary>
    /// Writes a blob to storage (upsert semantics).
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        await _client.WriteAsync(path, content, append: false, cancellationToken: ct);
        _logger?.LogDebug("Wrote blob to storage: {Path}", path);
    }

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        await _client.DeleteAsync(path, cancellationToken: ct);
        _logger?.LogDebug("Deleted blob from storage: {Path}", path);
    }

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    public async Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        return await _client.OpenReadAsync(path, cancellationToken: ct);
    }

    private async Task<IInterceptorSubject?> CreateSubjectFromBlobAsync(Blob blob, CancellationToken ct)
    {
        var context = ((IInterceptorSubject)this).Context;
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();

        // JSON files - check if [Configurable] type
        if (extension == ".json")
        {
            var json = await _client!.ReadTextAsync(blob.FullPath, cancellationToken: ct);

            // Try to deserialize as a configurable subject
            try
            {
                var subject = _serializer.Deserialize(json, context);
                if (subject != null)
                {
                    // Check if type has [Configurable] attribute
                    var hasConfigurable = subject.GetType().GetCustomAttributes(
                        typeof(ConfigurableAttribute), true).Length > 0;

                    if (hasConfigurable)
                        return subject;
                }
            }
            catch
            {
                // Not a configurable subject, fall through to JsonFile
            }

            // Create JsonFile for plain JSON
            return new Files.JsonFile(context, this, blob.FullPath) { Content = json };
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            return CreateFileSubject(mappedType, blob.FullPath, context);
        }

        // Default to GenericFile
        return CreateFileSubject(typeof(Files.GenericFile), blob.FullPath, context);
    }

    private IInterceptorSubject? CreateFileSubject(Type type, string blobPath, IInterceptorSubjectContext context)
    {
        try
        {
            // Try constructor with (context, storage, path)
            var ctor = type.GetConstructor(new[] { typeof(IInterceptorSubjectContext), typeof(Storage), typeof(string) });
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke(new object[] { context, this, blobPath });
            }

            // Fall back to constructor with just context
            ctor = type.GetConstructor(new[] { typeof(IInterceptorSubjectContext) });
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke(new object[] { context });
                // Set properties if available
                SetPropertyIfExists(subject, "Storage", this);
                SetPropertyIfExists(subject, "BlobPath", blobPath);
                SetPropertyIfExists(subject, "FilePath", blobPath);
                SetPropertyIfExists(subject, "FileName", Path.GetFileName(blobPath));
                return subject;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create file subject for: {Path}", blobPath);
        }

        return null;
    }

    private static void SetPropertyIfExists(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            try { prop.SetValue(target, value); } catch { }
        }
    }

    private void PlaceInHierarchy(string path, IInterceptorSubject subject)
    {
        // Normalize path separators
        path = path.Replace('\\', '/').TrimStart('/');
        var segments = path.Split('/');

        if (segments.Length == 1)
        {
            // Direct child of storage
            Children[segments[0]] = subject;
            return;
        }

        // Navigate/create folder hierarchy
        var children = Children;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!children.TryGetValue(folderName, out var existing))
            {
                var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
                var folder = new VirtualFolder(((IInterceptorSubject)this).Context, this, relativePath);
                children[folderName] = folder;
                children = folder.Children;
            }
            else if (existing is VirtualFolder vf)
            {
                children = vf.Children;
            }
            else
            {
                // Conflict - file exists where we need a folder
                _logger?.LogWarning("Path conflict at {Segment} for {Path}", folderName, path);
                return;
            }
        }

        children[segments[^1]] = subject;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
    }
}

/// <summary>
/// Status of a storage connection.
/// </summary>
public enum StorageStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded (VirtualFolder and JsonFile don't exist yet, so may have errors)

---

## Task 11: Create VirtualFolder Class

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs`

**Step 1: Create the VirtualFolder class**

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping that delegates operations to parent Storage.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : ITitleProvider, IIconProvider
{
    // MudBlazor Icons.Material.Filled.Folder
    private const string FolderIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z\" /></svg>";

    /// <summary>
    /// Reference to the root storage.
    /// </summary>
    public Storage Storage { get; }

    /// <summary>
    /// Relative path within the storage (e.g., "folder/subfolder/").
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Child subjects (files and folders).
    /// </summary>
    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public string? Title => Path.GetFileName(RelativePath.TrimEnd('/'));

    public string Icon => FolderIcon;

    public VirtualFolder(IInterceptorSubjectContext context, Storage storage, string relativePath)
    {
        Storage = storage;
        RelativePath = relativePath;
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    private string ResolvePath(string name) => RelativePath + name;

    /// <summary>
    /// Adds a new subject to storage at this folder's path.
    /// </summary>
    public Task AddSubjectAsync(string name, IInterceptorSubject subject, CancellationToken ct = default)
        => Storage.AddSubjectAsync(ResolvePath(name), subject, ct);

    /// <summary>
    /// Deletes a subject from storage.
    /// </summary>
    public Task DeleteSubjectAsync(string name, CancellationToken ct = default)
        => Storage.DeleteSubjectAsync(ResolvePath(name), ct);

    /// <summary>
    /// Writes a blob to storage (upsert semantics).
    /// </summary>
    public Task WriteBlobAsync(string name, Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(ResolvePath(name), content, ct);

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    public Task DeleteBlobAsync(string name, CancellationToken ct = default)
        => Storage.DeleteBlobAsync(ResolvePath(name), ct);

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    public Task<Stream> ReadBlobAsync(string name, CancellationToken ct = default)
        => Storage.ReadBlobAsync(ResolvePath(name), ct);
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded (JsonFile still missing)

---

## Task 12: Create JsonFile Class

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs`

**Step 1: Create the JsonFile class**

```csharp
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a JSON file in storage (non-configurable JSON).
/// </summary>
[InterceptorSubject]
public partial class JsonFile : ITitleProvider, IIconProvider
{
    // MudBlazor Icons.Material.Filled.DataObject
    private const string JsonIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M5,3H7V5H5V10A2,2 0 0,1 3,12A2,2 0 0,1 5,14V19H7V21H5C3.93,20.73 3,20.1 3,19V15A2,2 0 0,0 1,13H0V11H1A2,2 0 0,0 3,9V5A2,2 0 0,1 5,3M19,3A2,2 0 0,1 21,5V9A2,2 0 0,0 23,11H24V13H23A2,2 0 0,0 21,15V19A2,2 0 0,1 19,21H17V19H19V14A2,2 0 0,1 21,12A2,2 0 0,1 19,10V5H17V3H19Z\" /></svg>";

    /// <summary>
    /// Reference to the root storage.
    /// </summary>
    public Storage Storage { get; }

    /// <summary>
    /// Path within the storage.
    /// </summary>
    public string BlobPath { get; }

    /// <summary>
    /// JSON content of the file.
    /// </summary>
    [State]
    public partial string Content { get; set; }

    public string? Title => Path.GetFileNameWithoutExtension(BlobPath);

    public string Icon => JsonIcon;

    public JsonFile(IInterceptorSubjectContext context, Storage storage, string blobPath)
    {
        Storage = storage;
        BlobPath = blobPath;
        Content = string.Empty;
    }

    /// <summary>
    /// Saves the JSON content to storage.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Content));
        await Storage.WriteBlobAsync(BlobPath, stream, ct);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded

---

## Task 13: Update MarkdownFile with Storage Reference

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`

**Step 1: Add Storage and BlobPath properties**

Add after existing properties:
```csharp
/// <summary>
/// Reference to the root storage (set when loaded from Storage).
/// </summary>
public Storage? Storage { get; set; }

/// <summary>
/// Path within the storage (alternative to FilePath for storage-based files).
/// </summary>
public string? BlobPath { get; set; }
```

**Step 2: Add constructor accepting Storage**

Add new constructor:
```csharp
public MarkdownFile(IInterceptorSubjectContext context, Storage storage, string blobPath)
{
    Storage = storage;
    BlobPath = blobPath;
    FilePath = blobPath;
    FileName = Path.GetFileName(blobPath);
}
```

**Step 3: Add SaveAsync method**

```csharp
/// <summary>
/// Saves the content to storage (if loaded from storage).
/// </summary>
public async Task SaveAsync(CancellationToken ct = default)
{
    if (Storage != null && !string.IsNullOrEmpty(BlobPath))
    {
        var content = await GetContentAsync(ct);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await Storage.WriteBlobAsync(BlobPath, stream, ct);
    }
    else if (!string.IsNullOrEmpty(FilePath))
    {
        // Legacy file system path - content already saved via SetContentAsync
    }
}
```

**Step 4: Add using statement**

```csharp
using System.Text;
```

**Step 5: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded

---

## Task 14: Update GenericFile with Storage Reference

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/GenericFile.cs`

**Step 1: Read current GenericFile implementation**

Need to examine current implementation first.

**Step 2: Add Storage-based properties and methods**

Similar pattern to MarkdownFile - add Storage, BlobPath, constructor, and SaveAsync.

**Step 3: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded

---

## Task 15: Run Full Test Suite

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: All tests pass

**Step 2: Fix any failures**

If tests fail, investigate and fix.

---

## Task 16: Commit Batch 2 (Storage Implementation)

**Step 1: Stage and commit**

```bash
git add -A
git commit -m "feat: Implement Storage class with FluentStorage integration

- Add Storage class with FluentStorage backend
- Add VirtualFolder for hierarchical navigation
- Add JsonFile for non-configurable JSON files
- Update MarkdownFile with Storage support
- Update GenericFile with Storage support

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 17: Remove Old Storage Files

**Files:**
- Delete: `src/HomeBlaze/HomeBlaze.Storage/StorageContainer.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/FileSystemStorage.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/Folder.cs`

**Step 1: Update any imports/usages**

Search for usages of the old classes and update to use new Storage/VirtualFolder.

**Step 2: Delete the files**

Remove the old files.

**Step 3: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`
Expected: Build succeeded (may need to fix Program.cs if it uses old classes)

**Step 4: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Tests/HomeBlaze.Tests.csproj`
Expected: All tests pass

---

## Task 18: Final Integration Test

**Step 1: Start the application**

Run: `dotnet run --project src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`

**Step 2: Test Storage functionality**

1. Configure a Storage with a local path
2. Verify files are scanned and appear in tree
3. Modify a configurable subject
4. Verify auto-save via StorageService
5. Restart and verify persistence

---

## Task 19: Final Commit

**Step 1: Stage and commit**

```bash
git add -A
git commit -m "feat: Complete storage refactoring

- Remove old StorageContainer, FileSystemStorage, Folder
- Update Program.cs to use new Storage class
- Full integration testing complete

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Summary

| Batch | Tasks | Description |
|-------|-------|-------------|
| 1 | 1-9 | Foundation: FluentStorage, ConfigurableAttribute, ISubjectStorageHandler, StorageService, RootManager update |
| 2 | 10-16 | Storage Implementation: Storage, VirtualFolder, JsonFile, file type updates |
| 3 | 17-19 | Cleanup: Remove old files, integration testing, final commit |
