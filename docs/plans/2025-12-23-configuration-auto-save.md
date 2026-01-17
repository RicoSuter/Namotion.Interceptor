# Configuration Auto-Save Service Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Automatically persist `[Configuration]` property changes after 3 seconds of inactivity using a sliding window debounce per subject.

**Architecture:** A `BackgroundService` subscribes to the context's property change observable. Changes update a timestamp dictionary synchronously (fast), then `ObserveOn(TaskPoolScheduler)` moves processing to thread pool. `Throttle` waits for bursts to settle before processing pending saves. On shutdown, pending saves are flushed.

**Tech Stack:** .NET 9, System.Reactive, Namotion.Interceptor, Microsoft.Extensions.Hosting

---

### Task 1: Create ConfigurationWriterExtensions

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage.Abstractions/ConfigurationWriterExtensions.cs`
- Reference: `src/HomeBlaze/HomeBlaze.Host/Components/Editors/SubjectEditPanel.razor:136-172`

**Step 1: Create the extension class**

```csharp
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Extension methods for finding configuration writers in the subject hierarchy.
/// </summary>
public static class ConfigurationWriterExtensions
{
    /// <summary>
    /// Traverses the parent hierarchy to find the nearest IConfigurationWriter.
    /// </summary>
    /// <returns>The nearest writer, or null if none found.</returns>
    public static IConfigurationWriter? FindNearestConfigurationWriter(this IInterceptorSubject subject)
    {
        if (subject is IConfigurationWriter writer)
        {
            return writer;
        }

        var visited = new HashSet<IInterceptorSubject>();
        var current = subject;

        while (current != null)
        {
            if (!visited.Add(current))
            {
                break;
            }

            var parents = current.GetParents();
            if (parents.Count == 0)
            {
                break;
            }

            var parentSubject = parents.First().Property.Subject;
            if (parentSubject is IConfigurationWriter parentWriter)
            {
                return parentWriter;
            }

            current = parentSubject;
        }

        return null;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage.Abstractions`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Abstractions/ConfigurationWriterExtensions.cs
git commit -m "feat(homeblaze): extract FindNearestConfigurationWriter to shared extension"
```

---

### Task 2: Create ConfigurationAutoSaveService

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/ConfigurationAutoSaveService.cs`

**Step 1: Create the service class**

```csharp
using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Services;

/// <summary>
/// Background service that automatically saves configuration changes after a debounce period.
/// Uses Rx pipeline with per-subject sliding window debounce via timestamp dictionary.
/// </summary>
public class ConfigurationAutoSaveService : BackgroundService
{
    private readonly IInterceptorSubjectContext _context;
    private readonly RootManager _rootManager;
    private readonly ILogger<ConfigurationAutoSaveService> _logger;

    private readonly ConcurrentDictionary<IInterceptorSubject, DateTimeOffset> _scheduledSaves = new();
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(500);
    private IDisposable? _subscription;

    public ConfigurationAutoSaveService(
        IInterceptorSubjectContext context,
        RootManager rootManager,
        ILogger<ConfigurationAutoSaveService> logger)
    {
        _context = context;
        _rootManager = rootManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Dispose subscription when service stops
        stoppingToken.Register(() => _subscription?.Dispose());

        _subscription = _context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            // Fast filter - runs on publisher thread
            .Where(IsConfigurationProperty)
            // Fast dictionary update - runs on publisher thread
            .Do(ScheduleSave)
            // Hand off to thread pool - publisher is now free
            .ObserveOn(TaskPoolScheduler.Default)
            // Wait for rapid changes to settle
            .Throttle(_throttleInterval)
            // Process pending saves
            .SelectMany(_ => Observable.FromAsync(ProcessPendingSavesAsync))
            // Log errors (SaveSubjectAsync handles its own errors, this catches unexpected ones)
            .Do(_ => { }, ex => _logger.LogError(ex, "Unexpected error in auto-save pipeline"))
            .Retry()
            .Subscribe();

        // Keep service running until cancelled
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose subscription first to stop new changes coming in
        _subscription?.Dispose();
        _subscription = null;

        // Flush any pending saves before shutdown
        if (_scheduledSaves.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} pending auto-saves on shutdown", _scheduledSaves.Count);
            await FlushPendingSavesAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    private static bool IsConfigurationProperty(SubjectPropertyChange change)
        => change.Property.Metadata.Attributes.OfType<ConfigurationAttribute>().Any();

    private void ScheduleSave(SubjectPropertyChange change)
    {
        // Atomic sliding window update
        _scheduledSaves[change.Property.Subject] = DateTimeOffset.UtcNow.Add(_debounceDelay);
    }

    private async Task ProcessPendingSavesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Snapshot to avoid race conditions during iteration
        foreach (var kvp in _scheduledSaves.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Only save if debounce time has passed AND we can atomically claim it
            if (now >= kvp.Value && _scheduledSaves.TryRemove(kvp.Key, out _))
            {
                await SaveSubjectAsync(kvp.Key, cancellationToken);
            }
        }
    }

    private async Task FlushPendingSavesAsync(CancellationToken cancellationToken)
    {
        // Save all pending, regardless of debounce time
        foreach (var kvp in _scheduledSaves.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (_scheduledSaves.TryRemove(kvp.Key, out _))
            {
                await SaveSubjectAsync(kvp.Key, cancellationToken);
            }
        }
    }

    private async Task SaveSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        try
        {
            var writer = subject.FindNearestConfigurationWriter() ?? _rootManager;
            await writer.WriteConfigurationAsync(subject, cancellationToken);
            _logger.LogDebug("Auto-saved configuration for {SubjectType}", subject.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            // Service is stopping, ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-save configuration for {SubjectType}", subject.GetType().Name);
        }
    }

    /// <summary>
    /// Cancels any pending auto-save for the subject. Call this before manual saves.
    /// </summary>
    public void CancelPendingAutoSave(IInterceptorSubject subject)
    {
        _scheduledSaves.TryRemove(subject, out _);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/ConfigurationAutoSaveService.cs
git commit -m "feat(homeblaze): add ConfigurationAutoSaveService with Rx pipeline"
```

---

### Task 3: Register Service in DI

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs:28`

**Step 1: Add service registration**

After line 28 (`services.AddSingleton<ISubjectMethodInvoker, SubjectMethodInvoker>();`), add:

```csharp
services.AddSingleton<ConfigurationAutoSaveService>();
services.AddHostedService(sp => sp.GetRequiredService<ConfigurationAutoSaveService>());
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs
git commit -m "feat(homeblaze): register ConfigurationAutoSaveService in DI"
```

---

### Task 4: Update SubjectEditPanel to Use Shared Extension

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Host/Components/Editors/SubjectEditPanel.razor`

**Step 1: Add inject for AutoSaveService**

After line 11 (`@inject DeveloperModeService DeveloperMode`), add:

```csharp
@inject ConfigurationAutoSaveService AutoSaveService
```

**Step 2: Cancel pending auto-save before manual save**

In `SaveAsync()` method, before line 103 (`var configurationWriter = FindNearestConfigurationWriter(subject);`), add:

```csharp
// Cancel pending auto-save since we're doing a manual save
AutoSaveService.CancelPendingAutoSave(subject);
```

**Step 3: Simplify FindNearestConfigurationWriter method**

Replace lines 136-172 with:

```csharp
private IConfigurationWriter FindNearestConfigurationWriter(IInterceptorSubject subject)
{
    return subject.FindNearestConfigurationWriter() ?? RootManager;
}
```

**Step 4: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Host`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Host/Components/Editors/SubjectEditPanel.razor
git commit -m "feat(homeblaze): integrate auto-save cancellation in manual save"
```

---

### Task 5: Final Verification

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

---

## Pipeline Flow

```
Publisher Thread                      Thread Pool
─────────────────                     ──────────────────────────────

PropertyChange ──┐
                 │ Where()            (fast attribute check)
                 │ Do()               (fast dict write)
                 │
                 └─► ObserveOn() ────► Throttle(500ms)
                                       Wait for silence
                                       │
                                       ▼
                                       ProcessPendingSaves()
                                       - ToArray() snapshot
                                       - Check now >= saveTime
                                       - TryRemove + Save
```

## Graceful Shutdown

```
StopAsync() called
       │
       ▼
Dispose subscription (stop new changes)
       │
       ▼
FlushPendingSavesAsync() (save all pending, ignore debounce)
       │
       ▼
base.StopAsync()
```

## Characteristics

| Aspect | Value |
|--------|-------|
| Publisher impact | Minimal - fast filter + dict write |
| Processing thread | TaskPoolScheduler (background) |
| Throttle interval | 500ms |
| Debounce delay | 3 seconds per subject |
| Shutdown behavior | Flushes all pending saves |
| Race condition | Avoided via ToArray() snapshot |

## Verification Checklist

- [ ] Build passes
- [ ] Tests pass
- [ ] Manual save in UI still works
- [ ] Auto-save triggers 3s after config property change
- [ ] Rapid changes reset the 3s timer (sliding window)
- [ ] Manual save cancels pending auto-save
- [ ] Graceful shutdown flushes pending saves
