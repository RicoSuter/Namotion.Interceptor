# PR #104 Review: OPC UA Library Upgrade (1.5.376.244 → 1.5.378.65)

**Review Date:** 2025-12-19
**Branch:** feature/upgrade-opc-ua-library
**Status:** All 501 tests passing

---

## Executive Summary

This PR upgrades the OPC Foundation UA library with appropriate API adaptations. The changes are well-structured overall, but there are several critical issues that should be addressed before merging.

---

## Critical Issues (Must Fix)

### 1. Memory Leak in NullTelemetryContext.CreateMeter()

**File:** `src/Namotion.Interceptor.OpcUa/NullTelemetryContext.cs` (line 31)

**Problem:**
```csharp
public Meter CreateMeter() => new Meter("Namotion.Interceptor.OpcUa.Null");
```

Each call creates a new `Meter` instance that is never disposed. Meters should be singleton instances.

**Fix:**
```csharp
private static readonly Meter NullMeter = new("Namotion.Interceptor.OpcUa.Null");
public Meter CreateMeter() => NullMeter;
```

---

### 2. Async-over-Sync Anti-Pattern in Disposal

**File:** `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs` (lines 311-318)

**Problem:**
```csharp
try
{
    subscription.DeleteAsync(true).GetAwaiter().GetResult();
}
catch
{
    // Best effort during disposal
}
```

Issues:
- `.GetAwaiter().GetResult()` can cause deadlocks in some contexts
- Silent exception swallowing with no logging
- No timeout protection

**Fix:**
```csharp
foreach (var subscription in subscriptions)
{
    subscription.FastDataChangeCallback -= OnFastDataChange;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await subscription.DeleteAsync(true, cts.Token).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Failed to delete subscription {SubscriptionId} during disposal",
            subscription.Id);
    }
}
```

---

### 3. SessionManager.Dispose() Blocks on Async

**File:** `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` (lines 370-373)

**Problem:**
```csharp
public void Dispose()
{
    DisposeAsync().GetAwaiter().GetResult();
}
```

This violates best practices for implementing both sync and async disposal patterns.

**Fix:** Implement synchronous cleanup separately:
```csharp
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1)
        return;

    try { _reconnectHandler.Dispose(); } catch { }
    try { SubscriptionManager.Dispose(); } catch { }
    try { PollingManager?.Dispose(); } catch { }

    var sessionToDispose = _session;
    if (sessionToDispose is not null)
    {
        try { sessionToDispose.Dispose(); } catch { }
        Volatile.Write(ref _session, null);
    }
}
```

---

### 4. Breaking API Change Not Documented

**Files:**
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs` (line 218)
- `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs` (line 69)

**Change:**
```csharp
// Before (synchronous)
public virtual ApplicationInstance CreateApplicationInstance()

// After (asynchronous)
public virtual async Task<ApplicationInstance> CreateApplicationInstanceAsync()
```

**Impact:** Any code that overrides this method will break. This is a MAJOR version bump according to semantic versioning.

**Required Actions:**
- Document this as a breaking change in release notes
- Update the library version to reflect breaking changes

---

## High Priority Issues (Should Fix)

### 5. ActualSessionFactory Creates New Instance On Every Access

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs` (lines 211-216)

**Problem:**
```csharp
public ISessionFactory? SessionFactory { get; init; }

public ISessionFactory ActualSessionFactory =>
    SessionFactory ?? new DefaultSessionFactory(TelemetryContext);
```

Creates a `new DefaultSessionFactory` each time the property is accessed when `SessionFactory` is null.

**Fix:**
```csharp
private ISessionFactory? _resolvedSessionFactory;

public ISessionFactory ActualSessionFactory =>
    SessionFactory ?? _resolvedSessionFactory ??= new DefaultSessionFactory(TelemetryContext);
```

---

### 6. Missing Error Handling in Discovery

**File:** `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` (lines 93-98)

**Problem:**
```csharp
using var discoveryClient = await DiscoveryClient.CreateAsync(
    application.ApplicationConfiguration,
    serverUri,
    endpointConfiguration, ct: cancellationToken).ConfigureAwait(false);

var endpoints = await discoveryClient.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
```

No try-catch around discovery operations. Network failures here will bubble up unhandled.

**Recommendation:** Wrap in try-catch with clear error messages about discovery failures vs connection failures.

---

### 7. Telemetry Context Created Per-Registration

**File:** `src/Namotion.Interceptor.OpcUa/OpcUaSubjectExtensions.cs` (lines 75-76, 147-148)

**Problem:**
```csharp
var telemetryContext = DefaultTelemetry.Create(builder =>
    builder.Services.AddSingleton(loggerFactory));
```

Every call to `AddOpcUaSubjectClient` or `AddOpcUaSubjectServer` creates a new `DefaultTelemetry` instance with its own DI container. This is expensive and potentially wasteful.

**Recommendation:** Document the lifetime and disposal expectations, or consider reusing telemetry contexts.

---

## Medium Priority Issues

### 8. PollingManager.PollItemsAsync() - ToArray() Allocation

**File:** `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs` (line 243)

**Problem:**
```csharp
var itemsToRead = _pollingItems.Values.ToArray();
```

Allocates a new array on every poll interval. With default 1-second polling and hundreds of items, this creates continuous GC pressure.

**Fix:** Use `ArrayPool<T>.Shared`:
```csharp
var items = _pollingItems.Values;
var count = items.Count;
var buffer = ArrayPool<PollingItem>.Shared.Rent(count);
try
{
    items.CopyTo(buffer, 0);
    // Process buffer[0..count]
}
finally
{
    ArrayPool<PollingItem>.Shared.Return(buffer);
}
```

---

### 9. LINQ Allocation in CleanupPropertyDataForSubject

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` (lines 513-524)

**Problem:**
```csharp
var toRemove = _propertiesWithOpcData
    .Where(p => p.Subject == subject)
    .ToList();
```

Creates an intermediate `List<T>` allocation during subject detachment.

**Fix:** Use a simple loop:
```csharp
foreach (var property in _propertiesWithOpcData)
{
    if (property.Subject == subject)
    {
        property.RemovePropertyData(OpcUaNodeIdKey);
    }
}
_propertiesWithOpcData.RemoveWhere(p => p.Subject == subject);
```

---

### 10. Improved Error Message for Session Factory

**File:** `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` (lines 110-119)

**Current:**
```csharp
var newSession = await sessionFactory.CreateAsync(...).ConfigureAwait(false) as Session
    ?? throw new InvalidOperationException("Failed to create OPC UA session.");
```

**Recommended:**
```csharp
var sessionObject = await sessionFactory.CreateAsync(...).ConfigureAwait(false);
var newSession = sessionObject as Session
    ?? throw new InvalidOperationException(
        $"Session factory returned unexpected type: {sessionObject?.GetType().FullName ?? "null"}. " +
        $"Expected: {typeof(Session).FullName}");
```

---

## Low Priority Issues

### 11. Unused Compiler Constant

**File:** `src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`

```xml
<PropertyGroup Condition="'$(UseLocalOpcUaProjects)' == 'true'">
  <DefineConstants>$(DefineConstants);USE_LOCAL_OPCUA</DefineConstants>
</PropertyGroup>
```

The `USE_LOCAL_OPCUA` constant is defined but never used in any `#if` directives. Either use it or remove it.

---

### 12. Document ActivitySource Lifetime

**File:** `src/Namotion.Interceptor.OpcUa/NullTelemetryContext.cs` (line 20)

```csharp
private static readonly ActivitySource NullActivitySource = new("Namotion.Interceptor.OpcUa.Null");
```

`ActivitySource` implements `IDisposable` but is never disposed. Add XML comment documenting this is intentional for app-lifetime singletons.

---

### 13. Document Conditional Compilation Pattern

**File:** `src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`

Add comment explaining usage:
```xml
<!--
  Set UseLocalOpcUaProjects=true to develop against local OPC UA Foundation sources.
  Requires UA-.NETStandard repository cloned as sibling directory.
  Default: false (uses NuGet packages)
-->
<UseLocalOpcUaProjects>false</UseLocalOpcUaProjects>
```

---

## What's Working Well

### Positive Patterns

1. **Object Pooling in Hot Paths**
   ```csharp
   private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
       = new(() => new List<PropertyUpdate>(16));
   ```
   Excellent pooling in `OnFastDataChange` callback avoids per-notification allocations.

2. **Proper Volatile/Interlocked Usage** - Correct thread-safety patterns for session references and state flags.

3. **ReadOnlyMemory<T> Usage** - Avoids array copies in write paths.

4. **Pre-sized Collections** - Collections are properly pre-sized to avoid resize allocations.

5. **ConfigureAwait(false) Throughout** - All async continuations properly configured for library code.

6. **Consistent Client/Server Changes** - Changes applied uniformly across both implementations.

7. **Virtual Methods for Extensibility** - Configuration methods are virtual allowing customization.

---

## SOLID Principle Analysis

| Principle | Status | Notes |
|-----------|--------|-------|
| Single Responsibility | Warning | Configuration class creates `DefaultSessionFactory` |
| Open/Closed | Good | Virtual methods allow extension without modification |
| Liskov Substitution | Good | `NullTelemetryContext` correctly substitutes for any `ITelemetryContext` |
| Interface Segregation | Good | Uses focused interfaces (`ITelemetryContext`, `ISessionFactory`) |
| Dependency Inversion | Good | Depends on abstractions |

---

## Test Results

- **Total Tests:** 501
- **Passed:** 501
- **Failed:** 0
- **OPC UA Tests:** 37 (all passing)

---

## Files Changed

| File | Changes |
|------|---------|
| `Client/Connection/SessionManager.cs` | Endpoint discovery, session factory, telemetry |
| `Client/Connection/SubscriptionManager.cs` | Async disposal |
| `Client/OpcUaClientConfiguration.cs` | TelemetryContext, SessionFactory, async method |
| `Client/OpcUaSubjectClientSource.cs` | Async application instance |
| `Client/OpcUaSubjectLoader.cs` | Telemetry in MonitoredItem |
| `Namotion.Interceptor.OpcUa.csproj` | Version bump, conditional compilation |
| `NullTelemetryContext.cs` | **New file** |
| `OpcUaSubjectExtensions.cs` | Telemetry integration |
| `Server/OpcUaServerConfiguration.cs` | TelemetryContext, async method |
| `Server/OpcUaSubjectServerBackgroundService.cs` | Async methods |

---

## Recommendation

**Do not merge** until critical issues #1-4 are addressed. After fixes, this will be a solid upgrade that properly integrates the new OPC UA library features.

### Priority Order for Fixes

1. Fix `NullTelemetryContext.CreateMeter()` memory leak
2. Fix `SubscriptionManager.Dispose()` async-over-sync pattern
3. Fix `SessionManager.Dispose()` blocking pattern
4. Document breaking API changes
5. Fix `ActualSessionFactory` repeated instantiation
