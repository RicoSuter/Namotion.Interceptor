# HomeBlaze v2 Review Fixes Implementation Plan

## Overview

This plan addresses issues found during the in-depth review of HomeBlaze v2:
1. **Replace .NET reflection with registry APIs** - Use `RegisteredSubjectProperty.ReflectionAttributes` with caching
2. **Add file system monitoring** - Implement FileSystemWatcher with System.Reactive debouncing
3. **Align retry logic with Sources library** - Use exponential backoff pattern from MQTT client
4. **Add IReloadableConfiguration interface** - Update existing subjects instead of replacing them

---

## Part 1: Registry Extension Methods for HomeBlaze

### 1.1 Create HomeBlaze-specific registry extensions with caching

**File:** `HomeBlaze.Core/Extensions/SubjectRegistryExtensions.cs`

```csharp
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Core.Extensions;

/// <summary>
/// HomeBlaze-specific extension methods for the interceptor registry.
/// Uses registry APIs instead of .NET reflection for better performance.
/// </summary>
public static partial class SubjectRegistryExtensions
{
    // Cache by (SubjectType, PropertyName) -> attribute info
    // All instances of same subject type have identical attributes
    private static readonly ConcurrentDictionary<(Type, string), bool>
        _isConfigurationPropertyCache = new();

    private static readonly ConcurrentDictionary<(Type, string), StateAttribute?>
        _stateAttributeCache = new();

    private static readonly ConcurrentDictionary<string, string>
        _camelCaseCache = new();

    /// <summary>
    /// Gets all properties marked with [Configuration] attribute.
    /// </summary>
    public static IEnumerable<RegisteredSubjectProperty> GetConfigurationProperties(
        this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            yield break;

        foreach (var property in registered.Properties)
        {
            if (property.IsConfigurationProperty())
                yield return property;
        }
    }

    /// <summary>
    /// Gets all properties marked with [State] attribute.
    /// </summary>
    public static IEnumerable<RegisteredSubjectProperty> GetStateProperties(
        this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            yield break;

        foreach (var property in registered.Properties)
        {
            if (property.GetStateAttribute() != null)
                yield return property;
        }
    }

    /// <summary>
    /// Checks if a property has [Configuration] attribute.
    /// Uses cached lookup by (Type, PropertyName) for O(1) performance after first call.
    /// </summary>
    public static bool IsConfigurationProperty(this RegisteredSubjectProperty property)
    {
        var key = (property.Subject.GetType(), property.Name);
        return _isConfigurationPropertyCache.GetOrAdd(key, _ =>
        {
            foreach (var attr in property.ReflectionAttributes)
            {
                if (attr is ConfigurationAttribute)
                    return true;
            }
            return false;
        });
    }

    /// <summary>
    /// Gets the [State] attribute for a property, or null if not present.
    /// Uses cached lookup by (Type, PropertyName).
    /// </summary>
    public static StateAttribute? GetStateAttribute(this RegisteredSubjectProperty property)
    {
        var key = (property.Subject.GetType(), property.Name);
        return _stateAttributeCache.GetOrAdd(key, _ =>
        {
            foreach (var attr in property.ReflectionAttributes)
            {
                if (attr is StateAttribute sa)
                    return sa;
            }
            return null;
        });
    }

    /// <summary>
    /// Gets the display name for a property (from StateAttribute or camelCase split).
    /// </summary>
    public static string GetDisplayName(this RegisteredSubjectProperty property)
    {
        var stateAttr = property.GetStateAttribute();
        if (!string.IsNullOrEmpty(stateAttr?.Name))
            return stateAttr.Name;

        return SplitCamelCase(property.Name);
    }

    /// <summary>
    /// Gets the display order for a property (from StateAttribute).
    /// </summary>
    public static int GetDisplayOrder(this RegisteredSubjectProperty property)
    {
        return property.GetStateAttribute()?.Order ?? int.MaxValue;
    }

    /// <summary>
    /// Checks if a subject has any [Configuration] properties.
    /// </summary>
    public static bool HasConfigurationProperties(this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return false;

        foreach (var property in registered.Properties)
        {
            if (property.IsConfigurationProperty())
                return true;
        }
        return false;
    }

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();

    private static string SplitCamelCase(string input)
    {
        return _camelCaseCache.GetOrAdd(input, s => CamelCaseRegex().Replace(s, "$1 $2"));
    }
}
```

### 1.2 Update SubjectSerializer to use registry

**File:** `HomeBlaze.Core/Services/SubjectSerializer.cs`

Changes:
- Replace `type.GetProperties()...GetCustomAttribute<ConfigurationAttribute>()` with registry lookup
- Use `subject.GetConfigurationProperties()` for iteration

```csharp
// In SerializeSubject method, replace reflection with:
foreach (var regProperty in subject.GetConfigurationProperties())
{
    var value = regProperty.GetValue();
    var propertyName = JsonNamingPolicy.CamelCase.ConvertName(regProperty.Name);

    if (value == null)
    {
        writer.WriteNull(propertyName);
    }
    else if (value is IInterceptorSubject nestedSubject)
    {
        writer.WritePropertyName(propertyName);
        SerializeSubject(writer, nestedSubject);
    }
    else
    {
        writer.WritePropertyName(propertyName);
        JsonSerializer.Serialize(writer, value, value.GetType(), _jsonOptions);
    }
}
```

### 1.3 Update StorageService to use registry

**File:** `HomeBlaze.Core/Services/StorageService.cs`

Remove `_configurationPropertiesCache` static dictionary and use registry:

```csharp
private bool IsConfigurationProperty(PropertyReference property)
{
    var regProperty = property.TryGetRegisteredProperty();
    if (regProperty == null)
        return false;

    return regProperty.IsConfigurationProperty();
}
```

### 1.4 Update SubjectConfigurationDialog to use registry

**File:** `HomeBlaze\Components\SubjectConfigurationDialog.razor`

Replace reflection-based property discovery with registry extensions.

### 1.5 Remove SubjectPropertyPanel workaround

**File:** `HomeBlaze\Components\SubjectPropertyPanel.razor`

Remove the property access workaround (lines 235-248). Registry should auto-populate on subject creation.

---

## Part 2: File System Monitoring

### 2.1 IReloadableConfiguration interface

**File:** `HomeBlaze.Abstractions/IReloadableConfiguration.cs`

```csharp
namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that can reload their configuration from external sources.
/// Implement this to control how configuration updates are applied without
/// recreating the entire subject (preserves object references and runtime state).
/// </summary>
public interface IReloadableConfiguration
{
    /// <summary>
    /// Reloads configuration from the provided JSON.
    /// Called when the backing file changes externally.
    /// Only [Configuration] properties should be updated.
    /// </summary>
    /// <param name="json">The new JSON content from the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when reload is finished.</returns>
    Task ReloadConfigurationAsync(string json, CancellationToken cancellationToken = default);
}
```

### 2.2 Add file content hash tracking

**File:** `HomeBlaze.Storage/FluentStorageContainer.cs`

Track content hashes to detect actual changes:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;

// Fields
private FileSystemWatcher? _watcher;
private readonly Subject<FileSystemEventArgs> _fileEvents = new();
private IDisposable? _fileEventSubscription;
private readonly ConcurrentDictionary<string, string> _contentHashes = new(); // path -> SHA256 hash
private readonly ConcurrentDictionary<string, IInterceptorSubject> _pathToSubject = new(); // reverse lookup

/// <summary>
/// Whether file system watching is enabled. Default is true.
/// </summary>
[Configuration]
public partial bool EnableFileWatching { get; set; }
```

### 2.3 System.Reactive-based event processing

Replace `async void` handlers with Rx pipeline:

```csharp
private void StartFileWatching()
{
    var fullPath = Path.GetFullPath(ConnectionString);

    _watcher = new FileSystemWatcher(fullPath)
    {
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                       NotifyFilters.LastWrite | NotifyFilters.Size,
        IncludeSubdirectories = true,
        InternalBufferSize = 64 * 1024, // 64KB buffer to reduce overflow risk
        EnableRaisingEvents = true
    };

    // Route all events to the Rx subject
    _watcher.Created += (s, e) => _fileEvents.OnNext(e);
    _watcher.Changed += (s, e) => _fileEvents.OnNext(e);
    _watcher.Deleted += (s, e) => _fileEvents.OnNext(e);
    _watcher.Renamed += (s, e) => _fileEvents.OnNext(e);
    _watcher.Error += OnWatcherError;

    // Process events with proper debouncing using System.Reactive
    _fileEventSubscription = _fileEvents
        .GroupBy(e => e.FullPath)
        .SelectMany(group => group.Throttle(TimeSpan.FromSeconds(1)))
        .Where(e => !IsOwnWrite(e.FullPath)) // Ignore our own writes
        .Subscribe(
            onNext: async e => await ProcessFileEventAsync(e),
            onError: ex => _logger?.LogError(ex, "Error in file event stream"));

    _logger?.LogInformation("File watching enabled for: {Path}", fullPath);
}
```

### 2.4 Self-write tracking to prevent feedback loops

```csharp
private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingWrites = new();
private static readonly TimeSpan WriteGracePeriod = TimeSpan.FromSeconds(2);

/// <summary>
/// Marks a path as being written by us (prevents FileSystemWatcher feedback loop).
/// </summary>
private void MarkAsOwnWrite(string fullPath)
{
    _pendingWrites[fullPath] = DateTimeOffset.UtcNow;

    // Schedule cleanup after grace period
    _ = Task.Run(async () =>
    {
        await Task.Delay(WriteGracePeriod);
        _pendingWrites.TryRemove(fullPath, out _);
    });
}

private bool IsOwnWrite(string fullPath)
{
    if (_pendingWrites.TryGetValue(fullPath, out var writeTime))
    {
        return DateTimeOffset.UtcNow - writeTime < WriteGracePeriod;
    }
    return false;
}

// Update WriteAsync to mark own writes:
public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
{
    if (_client == null || !_subjectPaths.TryGetValue(subject, out var path))
        return false;

    var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
    MarkAsOwnWrite(fullPath);

    var json = _serializer.Serialize(subject);

    // Update content hash
    _contentHashes[path] = ComputeHash(json);

    await _client.WriteTextAsync(path, json, cancellationToken: ct);
    _logger?.LogDebug("Saved subject to storage: {Path}", path);
    return true;
}
```

### 2.5 Hash-based change detection and reload

```csharp
private async Task ProcessFileEventAsync(FileSystemEventArgs e)
{
    try
    {
        var relativePath = GetRelativePath(e.FullPath);

        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Created:
                await HandleFileCreatedAsync(relativePath, e.FullPath);
                break;

            case WatcherChangeTypes.Changed:
                await HandleFileChangedAsync(relativePath, e.FullPath);
                break;

            case WatcherChangeTypes.Deleted:
                HandleFileDeleted(relativePath);
                break;

            case WatcherChangeTypes.Renamed when e is RenamedEventArgs re:
                var oldRelativePath = GetRelativePath(re.OldFullPath);
                HandleFileDeleted(oldRelativePath);
                await HandleFileCreatedAsync(relativePath, e.FullPath);
                break;
        }
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Error processing file event: {Path} ({Type})",
            e.FullPath, e.ChangeType);
    }
}

private async Task HandleFileChangedAsync(string relativePath, string fullPath)
{
    // Check if we have this subject
    if (!_pathToSubject.TryGetValue(relativePath, out var existingSubject))
        return;

    // Read new content with retry (file may still be locked)
    string? newJson = null;
    for (int retry = 0; retry < 3; retry++)
    {
        try
        {
            newJson = await _client!.ReadTextAsync(relativePath);
            break;
        }
        catch (IOException) when (retry < 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, retry)));
        }
    }

    if (newJson == null)
    {
        _logger?.LogWarning("Could not read file after retries: {Path}", relativePath);
        return;
    }

    // Check if content actually changed via hash comparison
    var newHash = ComputeHash(newJson);
    if (_contentHashes.TryGetValue(relativePath, out var oldHash) && oldHash == newHash)
    {
        _logger?.LogDebug("File unchanged (same hash), skipping reload: {Path}", relativePath);
        return;
    }

    _contentHashes[relativePath] = newHash;

    // If subject implements IReloadableConfiguration, update in place
    if (existingSubject is IReloadableConfiguration reloadable)
    {
        await reloadable.ReloadConfigurationAsync(newJson);
        _logger?.LogInformation("Reloaded configuration for: {Path}", relativePath);
    }
    else
    {
        // Fallback: deserialize and copy [Configuration] properties
        await ReloadConfigurationPropertiesAsync(existingSubject, newJson);
        _logger?.LogInformation("Updated configuration properties for: {Path}", relativePath);
    }
}

private async Task ReloadConfigurationPropertiesAsync(IInterceptorSubject existing, string json)
{
    // Deserialize to temporary subject
    var context = ((IInterceptorSubject)this).Context;
    var tempSubject = _serializer.Deserialize(json, context);
    if (tempSubject == null)
        return;

    // Copy only [Configuration] properties from temp to existing
    foreach (var prop in existing.GetConfigurationProperties())
    {
        var tempProp = tempSubject.TryGetRegisteredSubject()?.TryGetProperty(prop.Name);
        if (tempProp != null)
        {
            var newValue = tempProp.GetValue();
            var oldValue = prop.GetValue();
            if (!Equals(newValue, oldValue))
            {
                prop.SetValue(newValue);
            }
        }
    }
}

private static string ComputeHash(string content)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
    var hash = SHA256.HashData(bytes);
    return Convert.ToHexString(hash);
}
```

### 2.6 File created and deleted handlers

```csharp
private async Task HandleFileCreatedAsync(string relativePath, string fullPath)
{
    _logger?.LogDebug("File created: {Path}", relativePath);

    var blob = new Blob(relativePath) { Kind = BlobItemKind.File };
    var subject = await CreateSubjectFromBlobAsync(blob, CancellationToken.None);

    if (subject != null)
    {
        // Read content for hash
        try
        {
            var content = await _client!.ReadTextAsync(relativePath);
            _contentHashes[relativePath] = ComputeHash(content);
        }
        catch { /* Non-text files won't have hash */ }

        _pathToSubject[relativePath] = subject;
        _subjectPaths[subject] = relativePath;

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        PlaceInHierarchy(relativePath, subject, children);
        Children = children;
    }
}

private void HandleFileDeleted(string relativePath)
{
    _logger?.LogDebug("File deleted: {Path}", relativePath);

    if (_pathToSubject.TryRemove(relativePath, out var subject))
    {
        _subjectPaths.TryRemove(subject, out _);
    }
    _contentHashes.TryRemove(relativePath, out _);

    var children = new Dictionary<string, IInterceptorSubject>(Children);
    RemoveFromHierarchy(relativePath, children);
    Children = children;
}
```

### 2.7 Watcher error handling with rescan

```csharp
private void OnWatcherError(object sender, ErrorEventArgs e)
{
    _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), triggering rescan");

    // Restart watcher
    _watcher?.Dispose();
    if (EnableFileWatching && Status == StorageStatus.Connected)
    {
        StartFileWatching();

        // Trigger full rescan to catch any missed events
        _ = Task.Run(async () =>
        {
            try
            {
                await ScanAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to rescan after watcher error");
            }
        });
    }
}
```

### 2.8 Update Dispose

```csharp
public void Dispose()
{
    _fileEventSubscription?.Dispose();
    _fileEvents.Dispose();
    _watcher?.Dispose();
    _watcher = null;
    _client?.Dispose();
    _client = null;
    Status = StorageStatus.Disconnected;
}
```

### 2.9 Update constructor and ScanAsync

```csharp
public FluentStorageContainer(...)
{
    // ...existing code...
    EnableFileWatching = true;
}

public async Task ScanAsync(CancellationToken ct = default)
{
    // ...existing scan code...

    // After scanning, populate reverse lookup and hashes
    foreach (var (subject, path) in _subjectPaths)
    {
        _pathToSubject[path] = subject;

        // Compute initial hashes for JSON files
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var content = await _client!.ReadTextAsync(path, cancellationToken: ct);
                _contentHashes[path] = ComputeHash(content);
            }
            catch { /* Ignore */ }
        }
    }
}
```

---

## Part 3: Retry Logic (Aligned with Sources Library)

### 3.1 Use exponential backoff pattern from Sources library

**File:** `HomeBlaze.Core/Services/StorageService.cs`

Align with the pattern used in `MqttConnectionMonitor.cs`:

```csharp
public class StorageService : BackgroundService
{
    private readonly ConcurrentDictionary<IInterceptorSubject, ISubjectStorageHandler> _subjectHandlers = new();
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<StorageService>? _logger;

    // Retry configuration (aligned with Sources library patterns)
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxRetryAttempts = 5;

    public StorageService(
        IInterceptorSubjectContext context,
        ILogger<StorageService>? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    // ... RegisterSubject, UnregisterSubject unchanged ...

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(100, stoppingToken);
        _logger?.LogInformation("StorageService background loop starting");

        var queue = _context.TryGetService<PropertyChangeQueue>();
        if (queue == null)
        {
            _logger?.LogWarning("PropertyChangeQueue not found. StorageService will not auto-save.");
            return;
        }

        using var subscription = queue.Subscribe();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                if (subscription.TryDequeue(out var change, linkedCts.Token))
                {
                    await ProcessChangeAsync(change, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Timeout - continue
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
        if (!IsConfigurationProperty(change.Property))
            return;

        var subject = change.Property.Subject;
        if (subject == null)
            return;

        _logger?.LogDebug("Configuration property changed: {Type}.{Property}",
            subject.GetType().Name, change.Property.Name);

        await WriteWithRetryAsync(subject, ct);
    }

    /// <summary>
    /// Writes subject with exponential backoff retry (pattern from Sources library).
    /// </summary>
    private async Task WriteWithRetryAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (!_subjectHandlers.TryGetValue(subject, out var handler))
            return;

        var delay = InitialRetryDelay;

        for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                if (await handler.WriteAsync(subject, ct))
                {
                    _logger?.LogDebug("Saved subject via {Handler}: {Type}",
                        handler.GetType().Name, subject.GetType().Name);
                }
                return; // Success
            }
            catch (IOException ex) when (attempt < MaxRetryAttempts)
            {
                // Exponential backoff with jitter (pattern from MqttConnectionMonitor)
                var jitter = Random.Shared.NextDouble() * 0.1 + 0.95; // 0.95 to 1.05
                var actualDelay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter);

                _logger?.LogWarning(
                    "I/O error saving {Type} (attempt {Attempt}/{Max}), retrying in {Delay}ms: {Message}",
                    subject.GetType().Name, attempt + 1, MaxRetryAttempts, (int)actualDelay.TotalMilliseconds, ex.Message);

                await Task.Delay(actualDelay, ct);

                // Double delay for next attempt, capped at max
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, MaxRetryDelay.TotalMilliseconds));
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to save {Type} after {Max} retries",
                    subject.GetType().Name, MaxRetryAttempts);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving subject {Type}", subject.GetType().Name);
                return; // Don't retry non-IO errors
            }
        }
    }

    private bool IsConfigurationProperty(PropertyReference property)
    {
        var regProperty = property.TryGetRegisteredProperty();
        return regProperty?.IsConfigurationProperty() ?? false;
    }
}
```

---

## Part 4: UI External Change Notification

### 4.1 Track editing state and notify on external changes

**File:** `HomeBlaze\Components\SubjectConfigurationDialog.razor`

```csharp
@implements IDisposable

@code {
    private IDisposable? _changeSubscription;
    private bool _externalChangeDetected;
    private bool _isSaving;

    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (Subject != null)
        {
            // Subscribe to property changes on this subject
            var observable = Subject.Context.GetPropertyChangeObservable();
            _changeSubscription = observable
                .Where(change => change.Property.Subject == Subject)
                .Where(change => change.Property.TryGetRegisteredProperty()?.IsConfigurationProperty() == true)
                .Subscribe(_ =>
                {
                    if (!_isSaving)
                    {
                        _externalChangeDetected = true;
                        InvokeAsync(StateHasChanged);
                    }
                });
        }
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
    }

    private async Task Save()
    {
        _isSaving = true;
        try
        {
            // ... existing save logic ...
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void ReloadValues()
    {
        if (Subject != null)
        {
            _configProperties = GetConfigurationProperties().ToList();
            _editValues = _configProperties.ToDictionary(
                p => p.Name,
                p => p.GetValue());
            _externalChangeDetected = false;
        }
    }
}
```

Add UI notification banner:

```razor
<MudDialog>
    <DialogContent>
        @if (_externalChangeDetected)
        {
            <MudAlert Severity="Severity.Warning" Class="mb-3">
                This configuration was modified externally.
                <MudButton Variant="Variant.Text" Color="Color.Warning" OnClick="ReloadValues">
                    Reload
                </MudButton>
            </MudAlert>
        }
        @* ... rest of form ... *@
    </DialogContent>
</MudDialog>
```

---

## Summary of Changes

| File | Changes |
|------|---------|
| `HomeBlaze.Abstractions/IReloadableConfiguration.cs` | **NEW** - Interface for in-place config reload |
| `HomeBlaze.Core/Extensions/SubjectRegistryExtensions.cs` | **NEW** - Registry extensions with (Type, string) caching |
| `HomeBlaze.Core/Services/SubjectSerializer.cs` | Replace reflection with registry APIs |
| `HomeBlaze.Core/Services/StorageService.cs` | Registry usage + exponential backoff (Sources pattern) |
| `HomeBlaze.Storage/FluentStorageContainer.cs` | FileSystemWatcher with Rx, hash tracking, IReloadableConfiguration |
| `HomeBlaze/Components/SubjectConfigurationDialog.razor` | Registry usage + external change notification |
| `HomeBlaze/Components/SubjectPropertyPanel.razor` | Remove property access workaround |

---

## Key Design Decisions

1. **Caching by (Type, PropertyName)** - All instances of same subject type share attribute cache
2. **Hash-based change detection** - SHA256 hash prevents redundant reloads
3. **IReloadableConfiguration** - Subjects can control their reload behavior; default fallback copies [Configuration] properties
4. **Self-write tracking** - 2-second grace period prevents FileSystemWatcher feedback loops
5. **System.Reactive debouncing** - Proper throttle per file path, no async void handlers
6. **Exponential backoff with jitter** - Aligned with Sources library (MqttConnectionMonitor pattern)
7. **Watcher error recovery** - Buffer overflow triggers full rescan

---

## Future Considerations (TODO)

1. **Polling for remote storage** - Add polling fallback for Azure/FTP storage types
2. **Empty folder cleanup** - Remove VirtualFolders when their last child is deleted
3. **Circuit breaker** - Consider adding `CircuitBreaker` from Sources library if persistent failures occur
