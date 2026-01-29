# TwinCAT ADS Connector Design

**Date:** 2025-01-28
**Status:** Ready for Implementation
**Package:** `Namotion.Interceptor.Connectors.TwinCAT`

## Summary

Design for a new connector that enables bidirectional synchronization between C# objects and Beckhoff TwinCAT PLCs via the ADS (Automation Device Specification) protocol.

## Motivation

When working with Beckhoff PLCs, OPC UA adds unnecessary overhead:
- App → OPC UA Server → ADS → PLC (current path with OPC UA)
- App → ADS → PLC (direct path with this connector)

Benefits: Lower latency, no OPC UA license required, direct control over read strategy.

## Dependencies

- `Beckhoff.TwinCAT.Ads` - Core ADS client library
- `Beckhoff.TwinCAT.Ads.Reactive` - Reactive extensions for notifications and polling
- `Namotion.Interceptor.Connectors` - Base connector infrastructure

## Documentation References

### Beckhoff ADS .NET Library

- **NuGet Package**: [Beckhoff.TwinCAT.Ads](https://www.nuget.org/packages/Beckhoff.TwinCAT.Ads)
- **API Documentation**: [Beckhoff TwinCAT.Ads .NET API](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/index.html)
- **Getting Started**: [TwinCAT.Ads Quick Start](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407515403.html)
- **GitHub Samples**: [Beckhoff TwinCAT.Ads Samples](https://github.com/Beckhoff/TF6000_ADS_DOTNET_V5_Samples)

### ADS Protocol

- **ADS Specification**: [ADS Protocol Introduction](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads_intro/index.html)
- **ADS Error Codes**: [ADS Return Codes](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads_intro/374277003.html)
- **ADS Device States**: [ADS State Machine](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads_intro/374310667.html)

### Reactive Extensions

- **NuGet Package**: [Beckhoff.TwinCAT.Ads.Reactive](https://www.nuget.org/packages/Beckhoff.TwinCAT.Ads.Reactive)
- **Reactive Documentation**: [TwinCAT.Ads.Reactive](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407519499.html)
- **WhenNotification**: [Device Notifications (Reactive)](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407544331.html)
- **PollValues**: [Polling Values (Reactive)](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407548427.html)

### Symbol Access

- **Symbol Loader**: [Dynamic Symbol Access](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407531147.html)
- **Sum Commands**: [ADS Sum Commands for Batch Operations](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads_intro/115847307.html)

### Data Types

- **PLC Data Types**: [IEC 61131-3 Data Types](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_plc_intro/2529399947.html)
- **Type Marshalling**: [.NET Type Marshalling](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads.net/9407523083.html)

### Testing

- **ADS Server Mock**: [dsian.TwinCAT.Ads.Server.Mock](https://github.com/densogiaichned/dsian.TwinCAT.Ads.Server.Mock) - Mock ADS server for unit testing

## Architecture Alignment

This connector follows the established patterns from OPC UA and MQTT connectors.

### Reused Infrastructure from `Namotion.Interceptor.Connectors`

| Class/Interface | Usage |
|-----------------|-------|
| `ISubjectSource` | Main interface to implement |
| `SubjectSourceBackgroundService` | Handles write queue, change processing, initialization sequence |
| `SubjectPropertyWriter` | Buffer-flush-load-replay pattern for inbound updates |
| `SourceOwnershipManager` | Property ownership tracking, lifecycle cleanup |
| `WriteResult` | Return type for `WriteChangesAsync` with partial failure support |
| `CircuitBreaker` | Reconnection storm prevention (in `Resilience/`) |
| `SourcePropertyExtensions` | `SetSource()`, `TryGetSource()`, `RemoveSource()` |
| `RegisteredSubjectPropertyExtensions` | `SetValueFromSource()` for inbound updates |
| `AttributeBasedPathProvider` | Path mapping via `[Path]` attributes (in `Registry.Paths`) |
| `PathAttribute` | Base class for `AdsVariableAttribute` (in `Registry.Attributes`) |

### ISubjectSource Interface

The connector must implement all `ISubjectSource` members:

```csharp
public interface ISubjectSource : ISubjectConnector
{
    IInterceptorSubject RootSubject { get; }
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    int WriteBatchSize { get; }
    Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken ct);
    ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken ct);
    Task<Action?> LoadInitialStateAsync(CancellationToken ct);
}
```

### Service Registration Pattern

Follow the same pattern as MQTT/OPC UA with extension methods:

```csharp
// Simple registration (AmsNetId defaults to hostname + ".1.1")
builder.Services.AddTwinCatSubjectClientSource<Machine>(
    host: "192.168.1.100",
    pathProviderName: "ads",
    amsPort: 851);

// Full configuration (explicit AmsNetId)
builder.Services.AddTwinCatSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new AdsClientConfiguration
    {
        Host = "192.168.1.100",
        AmsNetId = "192.168.1.100.1.1",  // Or custom AmsNetId from PLC config
        PathProvider = new AttributeBasedPathProvider("ads", '.')
    });
```

The registration adds:
1. `TwinCatSubjectClientSource` as `IHostedService` (for connection monitoring)
2. `SubjectSourceBackgroundService` as `IHostedService` (for write retry queue)

### Initialization Sequence

Follow the buffer-flush-load-replay pattern from `connectors.md`:

1. **Buffer**: During `StartListeningAsync()`, inbound updates are buffered
2. **Flush**: Pending writes from retry queue flushed to PLC
3. **Load**: `LoadInitialStateAsync()` reads current values from PLC
4. **Replay**: Buffered updates replayed in order after initial state

## Value Conversion

### AdsValueConverter Class

Handles type conversion between ADS/PLC types and .NET types. Uses virtual methods (like OPC UA) for extensibility:

```csharp
public class AdsValueConverter
{
    /// <summary>
    /// Converts a value received from the PLC to a .NET property value.
    /// Override for custom type mappings.
    /// </summary>
    public virtual object? ConvertToPropertyValue(object? adsValue, RegisteredSubjectProperty property)
    {
        if (adsValue is null) return null;

        var targetType = property.Type;

        // Handle DATE_AND_TIME → DateTimeOffset
        if (targetType == typeof(DateTimeOffset) && adsValue is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);

        // Handle TIME → TimeSpan (already native)
        // Handle arrays (element-wise conversion if needed)
        // Default: return as-is (most PLC types map directly)
        return adsValue;
    }

    /// <summary>
    /// Converts a .NET property value to an ADS-compatible value for writing to the PLC.
    /// Override for custom type mappings.
    /// </summary>
    public virtual object? ConvertToAdsValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is null) return null;

        // Handle DateTimeOffset → DateTime for DATE_AND_TIME
        if (propertyValue is DateTimeOffset dto)
            return dto.UtcDateTime;

        // Default: return as-is
        return propertyValue;
    }
}
```

### Default Type Mappings

The default `AdsValueConverter` handles standard PLC-to-.NET type conversions:

| PLC Type | .NET Type | Notes |
|----------|-----------|-------|
| BOOL | bool | Direct mapping |
| BYTE, USINT | byte | Direct mapping |
| SINT | sbyte | Direct mapping |
| WORD, UINT | ushort | Direct mapping |
| INT | short | Direct mapping |
| DWORD, UDINT | uint | Direct mapping |
| DINT | int | Direct mapping |
| LWORD, ULINT | ulong | Direct mapping |
| LINT | long | Direct mapping |
| REAL | float | Direct mapping |
| LREAL | double | Direct mapping |
| STRING | string | ASCII encoding (default) |
| WSTRING | string | UTF-16 encoding |
| DATE_AND_TIME, DT | DateTimeOffset | TwinCAT epoch conversion |
| TIME | TimeSpan | Milliseconds |
| Arrays | T[] | Element-wise conversion |

Custom converters can be provided for:
- Custom PLC structs
- Enum mappings
- Special string encodings

## Configuration

### AdsClientConfiguration

```csharp
public class AdsClientConfiguration
{
    // Connection
    public required string Host { get; set; }       // IP or hostname, e.g., "192.168.1.100" or "plc.local"
    public required string AmsNetId { get; set; }   // ADS device ID, e.g., "192.168.1.100.1.1" (typically IP + ".1.1")
    public int AmsPort { get; set; } = 851;         // TwinCAT3 PLC runtime port
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);  // ADS communication timeout

    // Path provider for property-to-symbol mapping
    public required IPathProvider PathProvider { get; set; }

    // Read strategy (global defaults)
    public AdsReadMode DefaultReadMode { get; set; } = AdsReadMode.Auto;
    public int DefaultCycleTime { get; set; } = 100;  // ms for notifications
    public int DefaultMaxDelay { get; set; } = 0;     // ms max delay for batching

    // Polling settings (for Polled mode)
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    // Resilience (used by SubjectSourceBackgroundService)
    public int WriteRetryQueueSize { get; set; } = 1000;
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    // Performance tuning
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);
    public TimeSpan RetryTime { get; set; } = TimeSpan.FromSeconds(1);

    // Circuit breaker (prevents reconnection storms)
    public int CircuitBreakerFailureThreshold { get; set; } = 5;  // Open after N consecutive failures
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(60);  // Wait before retrying

    // Value conversion
    public AdsValueConverter ValueConverter { get; set; } = new AdsValueConverter();

    public void Validate() { /* validation logic */ }
}
```

### AdsVariableAttribute

Extends `PathAttribute` for property-to-symbol mapping with ADS-specific options:

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class AdsVariableAttribute : PathAttribute
{
    public AdsVariableAttribute(string symbolPath, string? connectorName = null)
        : base(connectorName ?? AdsConstants.DefaultConnectorName, symbolPath)
    {
        SymbolPath = symbolPath;
    }

    public string SymbolPath { get; }
    public AdsReadMode ReadMode { get; init; } = AdsReadMode.Auto;
    public int CycleTime { get; init; } = int.MinValue;  // Uses global default if not set
    public int MaxDelay { get; init; } = int.MinValue;
}
```

### AdsReadMode

```csharp
public enum AdsReadMode
{
    Notification,  // Real-time ADS device notifications
    Polled,        // Periodic reads via PollValues
    Auto           // Notification with fallback to polled
}
```

## Read Mode Behavior

### Priority Order

| Priority | Mode | Behavior |
|----------|------|----------|
| 1st (protected) | `Notification` | Always real-time, never demoted |
| 2nd (flexible) | `Auto` | Starts as notification, first to be demoted to polling |
| 3rd (always polled) | `Polled` | Always bulk read, never uses notifications |

### Auto Mode Demotion

When ADS notification limits are reached:
1. Variables with **higher** `CycleTime` are demoted **first** (less time-sensitive)
2. Variables with **lower** `CycleTime` are demoted **last** (more time-sensitive)
3. `Notification` mode variables are never demoted

## Subject Graph Loading

The connector recursively loads the subject graph, similar to OPC UA's `OpcUaSubjectLoader`. It handles:
- **Scalar properties** - Direct value mapping
- **Subject references** - Nested objects
- **Collections** - Arrays/lists of subjects
- **Dictionaries** - Keyed collections of subjects

### Property Type Detection

Use existing `RegisteredSubjectProperty` helpers:
- `property.IsSubjectReference` → single nested subject (recursive)
- `property.IsSubjectCollection` → `IList<TSubject>` where T is subject (recursive)
- `property.IsSubjectDictionary` → `IDictionary<TKey, TSubject>` where value is subject (recursive)
- else → scalar value OR primitive array (single symbol)

**Important distinction:**
- **Subject collections** (`IList<Motor>`) → Each element is a subject, recursively loaded
- **Primitive arrays** (`int[]`, `float[]`) → Single ADS symbol, read/write as whole array

### Path Mapping

| C# Type | ADS Symbol Pattern | Example |
|---------|-------------------|---------|
| Scalar | `Parent.Property` | `GVL.Temperature` |
| Primitive array | `Parent.Property` | `GVL.Temperatures` (single symbol, ARRAY OF REAL) |
| Subject reference | `Parent.Child.Property` | `GVL.Motor.Speed` |
| Subject collection `[i]` | `Parent.Items[i].Property` | `GVL.Motors[0].Speed` |
| Subject dictionary `[key]` | `Parent.Dict.Key.Property` | `GVL.Devices.Pump1.Temperature` |

**TwinCAT examples:**
```
// Primitive array - single symbol, read/write as whole
GVL.Temperatures : ARRAY[0..9] OF REAL;  // Maps to float[] in C#

// Subject collection - each element is a struct with properties
GVL.Motors : ARRAY[0..3] OF ST_Motor;    // Maps to IList<Motor> in C#
// Paths: GVL.Motors[0].Speed, GVL.Motors[1].Speed, etc.

// Subject dictionary - keys become path segments
GVL.Devices.Pump1 : ST_Device;           // Maps to IDictionary<string, Device>
GVL.Devices.Pump2 : ST_Device;
// Paths: GVL.Devices.Pump1.Temperature, GVL.Devices.Pump2.Temperature
```

### Mapping Pseudo Code

```
FUNCTION LoadSubjectGraph(rootSubject, basePath):
    loadedSubjects = new HashSet()  // Cycle detection
    LoadSubjectRecursive(rootSubject, basePath, loadedSubjects)

FUNCTION LoadSubjectRecursive(subject, currentPath, loadedSubjects):
    IF subject IN loadedSubjects:
        RETURN  // Already processed (cycle)

    loadedSubjects.Add(subject)
    registeredSubject = subject.TryGetRegisteredSubject()

    FOR EACH property IN registeredSubject.Properties:
        IF NOT PathProvider.IsPropertyIncluded(property):
            CONTINUE

        propertySegment = PathProvider.TryGetPropertySegment(property)
        propertyPath = currentPath + "." + propertySegment

        IF property.IsSubjectReference:
            // Single nested subject (e.g., Motor property)
            // Path: GVL.Machine.Motor
            childSubject = property.Children.Single().Subject
            LoadSubjectRecursive(childSubject, propertyPath, loadedSubjects)

        ELSE IF property.IsSubjectCollection:
            // Collection of subjects (e.g., IList<Motor>)
            // Paths: GVL.Motors[0], GVL.Motors[1], etc.
            FOR i = 0 TO property.Children.Count - 1:
                childSubject = property.Children[i].Subject
                childPath = propertyPath + "[" + i + "]"
                LoadSubjectRecursive(childSubject, childPath, loadedSubjects)

        ELSE IF property.IsSubjectDictionary:
            // Dictionary of subjects (e.g., IDictionary<string, Device>)
            // Paths: GVL.Devices.Pump1, GVL.Devices.Pump2, etc.
            FOR EACH (key, childSubject) IN property.Children:
                childPath = propertyPath + "." + key
                LoadSubjectRecursive(childSubject, childPath, loadedSubjects)

        ELSE:
            // Scalar or primitive array - register as single ADS symbol
            // Path: GVL.Temperature or GVL.Temperatures (array)
            RegisterForNotificationOrPolling(property, propertyPath)
```

### Example Mapping

```csharp
// C# Model
[InterceptorSubject]
public partial class Machine
{
    [AdsVariable("GVL.Machine.Name")]
    public partial string Name { get; set; }              // Scalar

    [AdsVariable("GVL.Machine.Temperatures")]
    public partial float[] Temperatures { get; set; }     // Primitive array (single symbol)

    [AdsVariable("GVL.Machine.Motor")]
    public partial Motor Motor { get; set; }              // Subject reference

    [AdsVariable("GVL.Machine.Axes")]
    public partial IList<Axis> Axes { get; set; }         // Subject collection

    [AdsVariable("GVL.Machine.Devices")]
    public partial IDictionary<string, Device> Devices { get; set; }  // Subject dictionary
}

[InterceptorSubject]
public partial class Motor
{
    [AdsVariable("Speed")]
    public partial float Speed { get; set; }
}

[InterceptorSubject]
public partial class Axis
{
    [AdsVariable("Position")]
    public partial float Position { get; set; }
}

[InterceptorSubject]
public partial class Device
{
    [AdsVariable("Temperature")]
    public partial float Temperature { get; set; }
}
```

**Resulting ADS symbol registrations:**
```
GVL.Machine.Name                    → string (scalar)
GVL.Machine.Temperatures            → float[] (primitive array, single symbol)
GVL.Machine.Motor.Speed             → float (nested subject property)
GVL.Machine.Axes[0].Position        → float (collection element property)
GVL.Machine.Axes[1].Position        → float (collection element property)
GVL.Machine.Devices.Pump1.Temperature → float (dictionary entry property)
GVL.Machine.Devices.Pump2.Temperature → float (dictionary entry property)
```

### AdsSubjectLoader Implementation

```csharp
internal class AdsSubjectLoader
{
    private readonly AdsClientConfiguration _configuration;
    private readonly TwinCatSubjectClientSource _source;
    private readonly SourceOwnershipManager _ownership;

    public void LoadSubjectGraph(IInterceptorSubject rootSubject)
    {
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        LoadSubjectRecursive(rootSubject, null, loadedSubjects);
    }

    private void LoadSubjectRecursive(
        IInterceptorSubject subject,
        string? parentPath,
        HashSet<IInterceptorSubject> loadedSubjects)
    {
        if (!loadedSubjects.Add(subject))
            return;

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
            return;

        foreach (var property in registeredSubject.Properties)
        {
            if (!_configuration.PathProvider.IsPropertyIncluded(property))
                continue;

            var segment = _configuration.PathProvider.TryGetPropertySegment(property);
            if (segment is null)
                continue;

            var propertyPath = parentPath is null ? segment : $"{parentPath}.{segment}";

            if (property.IsSubjectReference)
            {
                var child = property.Children.SingleOrDefault();
                if (child.Subject is not null)
                {
                    LoadSubjectRecursive(child.Subject, propertyPath, loadedSubjects);
                }
            }
            else if (property.IsSubjectCollection)
            {
                var index = 0;
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}[{index}]";
                    LoadSubjectRecursive(child.Subject, childPath, loadedSubjects);
                    index++;
                }
            }
            else if (property.IsSubjectDictionary)
            {
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}.{child.Index}";
                    LoadSubjectRecursive(child.Subject, childPath, loadedSubjects);
                }
            }
            else
            {
                // Scalar or primitive array - single ADS symbol
                RegisterProperty(property, propertyPath);
            }
        }
    }

    private void RegisterProperty(RegisteredSubjectProperty property, string symbolPath)
    {
        if (!_ownership.ClaimSource(property.Reference))
            return;

        _source.RegisterForNotificationOrPolling(property, symbolPath);
    }
}
```

### Array Size Discovery

TwinCAT arrays have fixed sizes defined in the PLC. The connector discovers array dimensions via:

```csharp
// Read symbol metadata to get array dimensions
var symbolInfo = await _client.ReadSymbolAsync("GVL.Motors", cancellationToken);
// symbolInfo.ArrayDimensions = [{ LowerBound: 0, Length: 4 }]
```

This allows the connector to:
1. Create the correct number of child subjects
2. Register notifications/polling for each element
3. Build paths like `GVL.Motors[0].Speed`, `GVL.Motors[1].Speed`, etc.

## Implementation Details

### Class Structure

```csharp
internal sealed class TwinCatSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    // ADS client from Beckhoff library
    private AdsClient? _client;
    private SubjectPropertyWriter? _propertyWriter;

    // Caches - keyed by PropertyReference (stable), not RegisteredSubjectProperty (can become stale)
    private readonly ConcurrentDictionary<string, PropertyReference?> _symbolToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, uint> _propertyToHandle = new();
    private readonly CompositeDisposable _subscriptions = new();

    // ISubjectSource implementation
    public IInterceptorSubject RootSubject => _subject;
    public int WriteBatchSize => 0; // No limit

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => _configuration.PathProvider.IsPropertyIncluded(property);
}
```

### ADS Library Integration

Use the Reactive extensions directly:

```csharp
// For notifications - use WhenNotification
IDisposable subscription = _client
    .WhenNotification<T>(symbolPath, notificationSettings)
    .Subscribe(value => OnValueReceived(propertyReference, value));

// For polling - use PollValues
IDisposable subscription = symbol
    .PollValues(pollingInterval)
    .Subscribe(value => OnValueReceived(propertyReference, value));
```

### ISubjectSource Methods

```csharp
public async Task<IDisposable?> StartListeningAsync(
    SubjectPropertyWriter propertyWriter,
    CancellationToken cancellationToken)
{
    _propertyWriter = propertyWriter;

    // Connect to PLC via ADS router
    _client = new AdsClient();
    _client.Connect(
        _configuration.Host,
        AmsNetId.Parse(_configuration.AmsNetId),
        _configuration.AmsPort);

    // Register all properties
    var registeredSubject = _subject.TryGetRegisteredSubject();
    if (registeredSubject is null) return null;

    foreach (var property in registeredSubject.GetAllProperties())
    {
        if (!IsPropertyIncluded(property)) continue;
        if (!_ownership.ClaimSource(property.Reference)) continue;

        var symbolPath = GetSymbolPath(property);
        var mode = DetermineEffectiveReadMode(property);

        if (mode == AdsReadMode.Notification)
            RegisterNotification(property, symbolPath);
        else
            RegisterPolling(property, symbolPath);
    }

    return new CompositeDisposable(_subscriptions);
}

public async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
{
    // Read current values from PLC for all owned properties
    var values = new Dictionary<RegisteredSubjectProperty, object?>();

    foreach (var propertyRef in _ownership.Properties)
    {
        var registeredProperty = propertyRef.TryGetRegisteredProperty();
        if (registeredProperty is null) continue;

        var symbolPath = GetSymbolPath(registeredProperty);
        var adsValue = await _client.ReadValueAsync(symbolPath, cancellationToken);

        // Convert ADS value to .NET type
        var value = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);
        values[registeredProperty] = value;
    }

    return () =>
    {
        foreach (var (property, value) in values)
        {
            property.SetValueFromSource(this, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, value);
        }
    };
}

public async ValueTask<WriteResult> WriteChangesAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken)
{
    // See "Error Classification" section below for full implementation with
    // per-change error handling and transient/permanent error classification.
    // Simplified version shown here for clarity.
    try
    {
        foreach (var change in changes.Span)
        {
            var registeredProperty = change.Property.TryGetRegisteredProperty();
            if (registeredProperty is null) continue;

            var symbolPath = GetSymbolPath(registeredProperty);
            var adsValue = _configuration.ValueConverter.ConvertToAdsValue(
                change.GetNewValue<object?>(), registeredProperty);
            await _client.WriteValueAsync(symbolPath, adsValue, cancellationToken);
        }
        return WriteResult.Success;
    }
    catch (Exception ex)
    {
        return WriteResult.Failure(changes, ex);
    }
}
```

### Cache Invalidation Pattern

Critical for handling detached subjects:

1. **Cache `PropertyReference`, not `RegisteredSubjectProperty`** - `PropertyReference` is a stable identifier, while `RegisteredSubjectProperty` can become invalid when subjects are detached.

2. **Re-fetch on every callback** - When receiving ADS notifications or poll results, always re-fetch via `propertyReference.TryGetRegisteredProperty()` and skip if null.

3. **Validate after cache add** - To handle race conditions, add to cache first, then validate the subject is still attached. Remove if stale.

4. **Proactive cleanup on detach** - Use `SourceOwnershipManager.onSubjectDetaching` to clean up caches.

```csharp
// Constructor setup
_ownership = new SourceOwnershipManager(
    this,
    onReleasing: property =>
    {
        // Release ADS handle
        if (_propertyToHandle.TryRemove(property, out var handle))
            _client?.DeleteVariableHandle(handle);
    },
    onSubjectDetaching: subject =>
    {
        // Clean up symbol caches for detached subject
        foreach (var kvp in _symbolToProperty)
        {
            if (kvp.Value?.Subject == subject)
                _symbolToProperty.TryRemove(kvp.Key, out _);
        }
    });

// On notification received
private void OnValueReceived(PropertyReference propertyReference, object adsValue)
{
    var registeredProperty = propertyReference.TryGetRegisteredProperty();
    if (registeredProperty is null)
        return;  // Subject was detached, skip

    // Convert ADS value to .NET type
    var value = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);

    // Use static delegate to avoid allocations on hot path
    _propertyWriter?.Write(
        (propertyReference, value, this),
        static state => state.propertyReference.SetValueFromSource(
            state.Item3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            state.value));
}
```

### Service Registration

```csharp
public static class TwinCatSubjectExtensions
{
    public static IServiceCollection AddTwinCatSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string host,
        string pathProviderName,
        int amsPort = 851,
        string? amsNetId = null)  // Defaults to hostname + ".1.1"
        where TSubject : IInterceptorSubject
    {
        return services.AddTwinCatSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new AdsClientConfiguration
            {
                Host = host,
                AmsNetId = amsNetId ?? $"{host}.1.1",
                AmsPort = amsPort,
                PathProvider = new AttributeBasedPathProvider(pathProviderName, '.')
            });
    }

    public static IServiceCollection AddTwinCatSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, AdsClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new TwinCatSubjectClientSource(
                    subject,
                    sp.GetRequiredKeyedService<AdsClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<TwinCatSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<TwinCatSubjectClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<AdsClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<TwinCatSubjectClientSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }
}
```

## Limitations

### Static Object Graph

The connector uses a static model - the object graph structure is fixed at startup:
- Variables are discovered and registered during `StartListeningAsync`
- New subjects added after startup are NOT synchronized
- Removing subjects cleans up resources
- Re-adding subjects requires a restart

### ADS Notification Limits

TwinCAT has limits on concurrent ADS notifications. Auto mode handles this gracefully by demoting to polling.

### Client-Only

No server mode - this connector only connects to PLCs, it doesn't expose C# objects as ADS variables.

## Diagnostics

### AdsClientDiagnostics

Exposes runtime health information for monitoring:

```csharp
public class AdsClientDiagnostics
{
    private readonly TwinCatSubjectClientSource _source;

    internal AdsClientDiagnostics(TwinCatSubjectClientSource source) => _source = source;

    /// <summary>
    /// Current PLC state (Run, Stop, Config, Error, etc.).
    /// </summary>
    public AdsState? State => _source.CurrentState;

    /// <summary>
    /// Whether the ADS client is currently connected.
    /// </summary>
    public bool IsConnected => _source.IsConnected;

    /// <summary>
    /// Number of variables using notification mode.
    /// </summary>
    public int NotificationVariableCount => _source.NotificationCount;

    /// <summary>
    /// Number of variables using polling mode.
    /// </summary>
    public int PolledVariableCount => _source.PolledCount;

    /// <summary>
    /// Total reconnection attempts since startup.
    /// </summary>
    public long TotalReconnectionAttempts => _source.TotalReconnectionAttempts;

    /// <summary>
    /// Successful reconnections since startup.
    /// </summary>
    public long SuccessfulReconnections => _source.SuccessfulReconnections;

    /// <summary>
    /// Failed reconnections since startup.
    /// </summary>
    public long FailedReconnections => _source.FailedReconnections;

    /// <summary>
    /// Last successful connection time (null if never connected).
    /// </summary>
    public DateTimeOffset? LastConnectedAt => _source.LastConnectedAt;

    /// <summary>
    /// Whether the circuit breaker is currently open (blocking reconnection).
    /// </summary>
    public bool IsCircuitBreakerOpen => _source.CircuitBreaker.IsOpen;

    /// <summary>
    /// Number of times the circuit breaker has tripped.
    /// </summary>
    public long CircuitBreakerTripCount => _source.CircuitBreaker.TripCount;
}
```

Access via `TwinCatSubjectClientSource.Diagnostics` property.

## Resilience Features

### Reconnection Handling

The connector automatically handles disconnections and reconnects when the PLC becomes available again.

**Disconnection Detection:**
- `AdsClient.ConnectionStateChanged` event
- Failed read/write operations
- Health check loop in `ExecuteAsync`

**Reconnection Flow:**
1. Disconnection detected → start buffering inbound updates
2. Wait for `ReconnectInterval` before attempting reconnection
3. Reconnect to PLC via ADS router
4. Re-register all notifications and polling subscriptions
5. Flush pending writes from retry queue
6. Load current state via `LoadInitialStateAsync`
7. Replay buffered updates

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            if (_isStarted && !IsConnected && !_circuitBreaker.IsOpen)
            {
                await ReconnectAsync(stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(_configuration.HealthCheckInterval, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed. Retrying after delay.");
            _circuitBreaker.RecordFailure();
        }
    }
}
```

### Circuit Breaker

Prevents reconnection storms during prolonged outages. Uses the existing `CircuitBreaker` class from `Namotion.Interceptor.Connectors.Resilience`:

```csharp
// In TwinCatSubjectClientSource constructor
_circuitBreaker = new CircuitBreaker(
    _configuration.CircuitBreakerFailureThreshold,
    _configuration.CircuitBreakerCooldown);

// In reconnection logic
if (!_circuitBreaker.ShouldAttempt())
{
    _logger.LogDebug("Circuit breaker open, skipping reconnection attempt. Cooldown remaining: {Remaining}",
        _circuitBreaker.GetCooldownRemaining());
    return;
}

try
{
    await ReconnectAsync(cancellationToken);
    _circuitBreaker.RecordSuccess();
}
catch (Exception ex)
{
    if (_circuitBreaker.RecordFailure())
    {
        _logger.LogWarning("Circuit breaker opened after {Count} consecutive failures.",
            _configuration.CircuitBreakerFailureThreshold);
    }
}
```

### Error Classification

Distinguishes transient errors (retry) from permanent errors (don't retry):

```csharp
internal static class AdsErrorClassifier
{
    /// <summary>
    /// Determines if an ADS error is transient and should be retried.
    /// </summary>
    public static bool IsTransientError(AdsErrorCode errorCode)
    {
        // Permanent errors - don't retry
        return errorCode switch
        {
            AdsErrorCode.DeviceSymbolNotFound => false,      // Symbol doesn't exist
            AdsErrorCode.DeviceInvalidSize => false,         // Type mismatch
            AdsErrorCode.DeviceInvalidData => false,         // Invalid data format
            AdsErrorCode.DeviceNotSupported => false,        // Operation not supported
            AdsErrorCode.DeviceInvalidAccess => false,       // Access denied
            AdsErrorCode.DeviceInvalidOffset => false,       // Invalid memory offset

            // Transient errors - retry
            AdsErrorCode.TargetPortNotFound => true,         // Connection issue
            AdsErrorCode.TargetMachineNotFound => true,      // Network issue
            AdsErrorCode.ClientPortNotOpen => true,          // Client not connected
            AdsErrorCode.DeviceError => true,                // Generic device error
            AdsErrorCode.DeviceTimeout => true,              // Timeout
            AdsErrorCode.DeviceBusy => true,                 // Server overloaded

            // Default: treat unknown errors as transient (safer)
            _ => true
        };
    }
}
```

Used in `WriteChangesAsync` to classify partial failures:

```csharp
public async ValueTask<WriteResult> WriteChangesAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken)
{
    var failedChanges = new List<SubjectPropertyChange>();
    var transientCount = 0;

    foreach (var change in changes.Span)
    {
        try
        {
            var symbolPath = GetSymbolPath(change.Property);
            var value = _configuration.ValueConverter.ConvertToAdsValue(
                change.GetNewValue<object?>(),
                change.Property.TryGetRegisteredProperty()!);
            await _client.WriteValueAsync(symbolPath, value, cancellationToken);
        }
        catch (AdsException ex)
        {
            failedChanges.Add(change);
            if (AdsErrorClassifier.IsTransientError(ex.ErrorCode))
                transientCount++;
        }
    }

    if (failedChanges.Count == 0)
        return WriteResult.Success;

    var error = new AdsWriteException(transientCount, failedChanges.Count - transientCount, changes.Length);
    return failedChanges.Count == changes.Length
        ? WriteResult.Failure(failedChanges.ToArray(), error)
        : WriteResult.PartialFailure(failedChanges.ToArray(), error);
}
```

## ADS Concepts Integration

### Device State (AdsState)

The PLC has states: `Run`, `Stop`, `Config`, `Error`, etc. The connector should:

1. **Check state on connect** - Verify PLC is in `Run` state before registering notifications
2. **Monitor state changes** - Detect when PLC transitions (e.g., Run → Stop → Run after program download)
3. **Expose in diagnostics** - `Diagnostics.State` property

```csharp
// In TwinCatSubjectClientSource
public async Task<bool> IsPlcRunningAsync(CancellationToken ct)
{
    var state = await _client.ReadStateAsync(ct);
    return state.AdsState == AdsState.Run;
}

// State change notification for reconnection logic
_client.AdsStateChanged += (sender, e) =>
{
    if (e.State.AdsState == AdsState.Run)
        _logger.LogInformation("PLC entered Run state");
    else
        _logger.LogWarning("PLC left Run state: {State}", e.State.AdsState);
};
```

### Symbol Handles vs Symbol Paths

For performance, ADS supports pre-acquiring handles for symbols:
- `CreateVariableHandle(symbolPath)` → returns `uint` handle
- Read/write via handle is faster than by path

**Decision:** Start with symbol paths (simpler), add handle caching as optimization if needed.

### Symbol Discovery and Attributes

ADS supports runtime symbol discovery via `SymbolLoaderFactory`:

```csharp
var loader = SymbolLoaderFactory.Create(_client, SymbolLoaderSettings.Default);
var symbols = loader.Symbols;  // All PLC symbols

foreach (var symbol in symbols)
{
    Console.WriteLine($"Name: {symbol.InstancePath}");
    Console.WriteLine($"Type: {symbol.TypeName}");
    Console.WriteLine($"Comment: {symbol.Comment}");

    // User-defined attributes from PLC pragmas
    foreach (var attr in symbol.Attributes)
    {
        Console.WriteLine($"Attribute: {attr.Name} = {attr.Value}");
    }
}
```

PLC variables can have attributes defined via pragmas:
```iec
{attribute 'ReadMode' := 'Notification'}
{attribute 'CycleTime' := '10'}
temperature : REAL;  // Comment: Main temperature sensor
```

**Important:** ADS pragma attributes are **static compile-time metadata** (like .NET attributes), NOT runtime values. They are different from Namotion.Interceptor property attributes which are trackable runtime values (like `Pressure_Minimum`, `Unit`).

| | Namotion.Interceptor Attributes | ADS Pragma Attributes |
|---|---|---|
| Nature | Runtime values, trackable | Static compile-time metadata |
| Syncable | Yes | No, read-only |
| Example | `EURange`, `Unit` values | `{attribute 'TcLinkTo'}` |

**Potential uses for ADS attributes (internal only):**
1. **Connector configuration** - Read `ReadMode`, `CycleTime` from PLC attributes
2. **Documentation** - Use `Comment` for logging/diagnostics

**NOT mapped to property attributes** - unlike OPC UA's `EURange`/`EngineeringUnits` which are runtime values.

**Decision:** v1 uses static C# model with `[AdsVariable]` attributes. Dynamic discovery is a future enhancement.

## Future Enhancements

### SumSymbolRead Batching

Current implementation uses `PollValues` which polls each symbol individually. For very large variable sets (1000+), implement bulk reads using ADS Sum commands (Index Group 0xF080).

### Symbol Handle Caching

Cache variable handles (`CreateVariableHandle`) instead of using symbol paths for read/write operations. Handles provide faster repeated access.

### Symbol Version Monitoring

Monitor PLC symbol version changes to detect when PLC program is updated (download vs online change). Could trigger automatic re-initialization of subscriptions.

### Dynamic Symbol Discovery

Like OPC UA, support discovering PLC symbols at runtime and adding them as dynamic properties:
- Browse symbols via `SymbolLoaderFactory`
- Create dynamic properties for symbols not in C# model
- Read configuration from PLC attributes (e.g., `{attribute 'ReadMode' := 'Notification'}`)

### Dynamic Structural Synchronization

Support dynamic addition/removal of variables at runtime. This requires careful design across all connectors to ensure consistent behavior.

## Testing Strategy

Use [dsian.TwinCAT.Ads.Server.Mock](https://github.com/densogiaichned/dsian.TwinCAT.Ads.Server.Mock) NuGet package to mock ADS server for unit tests:

```xml
<PackageReference Include="dsian.TwinCAT.Ads.Server.Mock" Version="0.7.1" />
```

### Value Converter Tests

```csharp
public class AdsValueConverterTests
{
    private readonly AdsValueConverter _converter = new();

    [Theory]
    [InlineData(true, typeof(bool))]
    [InlineData((byte)42, typeof(byte))]
    [InlineData((short)1234, typeof(short))]
    [InlineData(12345, typeof(int))]
    [InlineData(123456L, typeof(long))]
    [InlineData(3.14f, typeof(float))]
    [InlineData(3.14159, typeof(double))]
    [InlineData("hello", typeof(string))]
    public void ConvertToPropertyValue_Primitives_ShouldPassThrough(object adsValue, Type expectedType)
    {
        var property = CreateMockProperty(expectedType);
        var result = _converter.ConvertToPropertyValue(adsValue, property);
        Assert.Equal(adsValue, result);
    }

    [Fact]
    public void ConvertToPropertyValue_DateTime_ShouldConvertToDateTimeOffset()
    {
        var dateTime = new DateTime(2025, 1, 28, 12, 0, 0, DateTimeKind.Utc);
        var property = CreateMockProperty(typeof(DateTimeOffset));

        var result = _converter.ConvertToPropertyValue(dateTime, property);

        Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(new DateTimeOffset(dateTime, TimeSpan.Zero), result);
    }

    [Fact]
    public void ConvertToAdsValue_DateTimeOffset_ShouldConvertToDateTime()
    {
        var dto = new DateTimeOffset(2025, 1, 28, 12, 0, 0, TimeSpan.FromHours(1));
        var property = CreateMockProperty(typeof(DateTimeOffset));

        var result = _converter.ConvertToAdsValue(dto, property);

        Assert.IsType<DateTime>(result);
        Assert.Equal(dto.UtcDateTime, result);
    }

    [Fact]
    public void ConvertToPropertyValue_IntArray_ShouldPassThrough()
    {
        var array = new[] { 1, 2, 3, 4, 5 };
        var property = CreateMockProperty(typeof(int[]));

        var result = _converter.ConvertToPropertyValue(array, property);

        Assert.Equal(array, result);
    }

    [Fact]
    public void ConvertToPropertyValue_Null_ShouldReturnNull()
    {
        var property = CreateMockProperty(typeof(string));
        var result = _converter.ConvertToPropertyValue(null, property);
        Assert.Null(result);
    }
}
```

### Error Classifier Tests

```csharp
public class AdsErrorClassifierTests
{
    [Theory]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound, false)]  // Permanent
    [InlineData(AdsErrorCode.DeviceInvalidSize, false)]     // Permanent
    [InlineData(AdsErrorCode.DeviceInvalidData, false)]     // Permanent
    [InlineData(AdsErrorCode.DeviceNotSupported, false)]    // Permanent
    [InlineData(AdsErrorCode.DeviceInvalidAccess, false)]   // Permanent
    [InlineData(AdsErrorCode.DeviceInvalidOffset, false)]   // Permanent
    public void IsTransientError_PermanentErrors_ShouldReturnFalse(AdsErrorCode errorCode, bool expected)
    {
        var result = AdsErrorClassifier.IsTransientError(errorCode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(AdsErrorCode.TargetPortNotFound, true)]     // Transient
    [InlineData(AdsErrorCode.TargetMachineNotFound, true)]  // Transient
    [InlineData(AdsErrorCode.ClientPortNotOpen, true)]      // Transient
    [InlineData(AdsErrorCode.DeviceError, true)]            // Transient
    [InlineData(AdsErrorCode.DeviceTimeout, true)]          // Transient
    [InlineData(AdsErrorCode.DeviceBusy, true)]             // Transient
    public void IsTransientError_TransientErrors_ShouldReturnTrue(AdsErrorCode errorCode, bool expected)
    {
        var result = AdsErrorClassifier.IsTransientError(errorCode);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsTransientError_UnknownError_ShouldReturnTrue()
    {
        // Unknown errors default to transient (safer to retry)
        var result = AdsErrorClassifier.IsTransientError((AdsErrorCode)99999);
        Assert.True(result);
    }
}
```

### Attribute and Path Tests

```csharp
public class AdsVariableAttributeTests
{
    [Fact]
    public void Constructor_ShouldSetSymbolPath()
    {
        var attr = new AdsVariableAttribute("GVL.Temperature");

        Assert.Equal("GVL.Temperature", attr.SymbolPath);
        Assert.Equal("GVL.Temperature", attr.Path);
        Assert.Equal(AdsReadMode.Auto, attr.ReadMode);
    }

    [Fact]
    public void Constructor_WithConnectorName_ShouldSetName()
    {
        var attr = new AdsVariableAttribute("GVL.Temperature", "plc1");

        Assert.Equal("plc1", attr.Name);
    }

    [Fact]
    public void ReadMode_ShouldBeConfigurable()
    {
        var attr = new AdsVariableAttribute("GVL.Temperature")
        {
            ReadMode = AdsReadMode.Notification,
            CycleTime = 50,
            MaxDelay = 10
        };

        Assert.Equal(AdsReadMode.Notification, attr.ReadMode);
        Assert.Equal(50, attr.CycleTime);
        Assert.Equal(10, attr.MaxDelay);
    }
}

public class PathProviderTests
{
    [Fact]
    public void IsPropertyIncluded_WithMatchingAttribute_ShouldReturnTrue()
    {
        var provider = new AttributeBasedPathProvider("ads", '.');
        var property = CreatePropertyWithAttribute(new AdsVariableAttribute("GVL.Temp", "ads"));

        Assert.True(provider.IsPropertyIncluded(property));
    }

    [Fact]
    public void IsPropertyIncluded_WithoutAttribute_ShouldReturnFalse()
    {
        var provider = new AttributeBasedPathProvider("ads", '.');
        var property = CreatePropertyWithoutAttribute();

        Assert.False(provider.IsPropertyIncluded(property));
    }
}
```

### Read Mode Demotion Tests

```csharp
public class ReadModeDemotionTests
{
    [Fact]
    public void DetermineEffectiveReadMode_NotificationMode_ShouldNeverDemote()
    {
        var source = CreateSource(notificationLimit: 0);  // No slots available
        var property = CreatePropertyWithReadMode(AdsReadMode.Notification, cycleTime: 100);

        var result = source.DetermineEffectiveReadMode(property);

        Assert.Equal(AdsReadMode.Notification, result);  // Protected, never demoted
    }

    [Fact]
    public void DetermineEffectiveReadMode_AutoMode_ShouldDemoteWhenLimitReached()
    {
        var source = CreateSource(notificationLimit: 0);  // No slots available
        var property = CreatePropertyWithReadMode(AdsReadMode.Auto, cycleTime: 100);

        var result = source.DetermineEffectiveReadMode(property);

        Assert.Equal(AdsReadMode.Polled, result);  // Demoted to polling
    }

    [Fact]
    public void DetermineEffectiveReadMode_AutoMode_ShouldDemoteHighCycleTimeFirst()
    {
        var source = CreateSource(notificationLimit: 1);  // Only 1 slot
        var fastProperty = CreatePropertyWithReadMode(AdsReadMode.Auto, cycleTime: 10);
        var slowProperty = CreatePropertyWithReadMode(AdsReadMode.Auto, cycleTime: 1000);

        // Register fast property first (gets the slot)
        source.RegisterProperty(fastProperty);
        source.RegisterProperty(slowProperty);

        // Fast property keeps notification, slow property demoted
        Assert.Equal(AdsReadMode.Notification, source.GetEffectiveReadMode(fastProperty));
        Assert.Equal(AdsReadMode.Polled, source.GetEffectiveReadMode(slowProperty));
    }

    [Fact]
    public void DetermineEffectiveReadMode_PolledMode_ShouldAlwaysPoll()
    {
        var source = CreateSource(notificationLimit: 100);  // Plenty of slots
        var property = CreatePropertyWithReadMode(AdsReadMode.Polled, cycleTime: 10);

        var result = source.DetermineEffectiveReadMode(property);

        Assert.Equal(AdsReadMode.Polled, result);  // Always polled as requested
    }
}
```

### Cache Invalidation Tests

```csharp
public class CacheInvalidationTests
{
    [Fact]
    public async Task OnSubjectDetached_ShouldRemoveFromSymbolCache()
    {
        // Arrange
        using var mockServer = new AdsServerMock();
        var source = CreateTwinCatClientSource(mockServer.ServerAddress);
        var subject = CreateSubjectWithProperties();

        await source.StartAsync(CancellationToken.None);

        // Act - Detach subject
        subject.Context.DetachSubject(subject);

        // Assert - Symbol cache should be cleared
        Assert.False(source.HasCachedSymbols(subject));
    }

    [Fact]
    public void OnValueReceived_WhenSubjectDetached_ShouldSkipUpdate()
    {
        // Arrange
        using var mockServer = new AdsServerMock();
        var source = CreateTwinCatClientSource(mockServer.ServerAddress);
        var subject = CreateSubjectWithProperties();
        var propertyRef = subject.GetPropertyReference("Temperature");

        // Detach subject
        subject.Context.DetachSubject(subject);

        // Act - Simulate notification received for detached property
        var updateCount = 0;
        source.OnValueReceived(propertyRef, 99.0f, () => updateCount++);

        // Assert - Update should be skipped
        Assert.Equal(0, updateCount);
    }

    [Fact]
    public async Task OnReleasing_ShouldDeleteVariableHandle()
    {
        // Arrange
        using var mockServer = new AdsServerMock();
        var deletedHandles = new List<uint>();
        mockServer.OnDeleteHandle = handle => deletedHandles.Add(handle);

        var source = CreateTwinCatClientSource(mockServer.ServerAddress);
        await source.StartAsync(CancellationToken.None);

        var property = GetRegisteredProperty(source);
        var handle = source.GetHandle(property);

        // Act - Release property
        source.ReleaseProperty(property);

        // Assert - Handle should be deleted
        Assert.Contains(handle, deletedHandles);
    }
}
```

### Service Registration Tests

```csharp
public class TwinCatSubjectExtensionsTests
{
    [Fact]
    public void AddTwinCatSubjectClientSource_ShouldRegisterServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Machine>();

        services.AddTwinCatSubjectClientSource<Machine>(
            host: "192.168.1.100",
            pathProviderName: "ads",
            amsPort: 851);

        var provider = services.BuildServiceProvider();

        // Should register both hosted services
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Equal(2, hostedServices.Count);
        Assert.Contains(hostedServices, s => s is TwinCatSubjectClientSource);
        Assert.Contains(hostedServices, s => s is SubjectSourceBackgroundService);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_WithCustomConfig_ShouldUseConfig()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Machine>();

        services.AddTwinCatSubjectClientSource(
            subjectSelector: sp => sp.GetRequiredService<Machine>(),
            configurationProvider: sp => new AdsClientConfiguration
            {
                Host = "10.0.0.1",
                AmsNetId = "10.0.0.1.1.1",
                AmsPort = 852,
                DefaultReadMode = AdsReadMode.Polled,
                PathProvider = new AttributeBasedPathProvider("custom", '.')
            });

        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal(852, source.Configuration.AmsPort);
        Assert.Equal(AdsReadMode.Polled, source.Configuration.DefaultReadMode);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_DefaultAmsNetId_ShouldAppendOneOne()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Machine>();

        services.AddTwinCatSubjectClientSource<Machine>(
            host: "192.168.1.100",
            pathProviderName: "ads");

        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("192.168.1.100.1.1", source.Configuration.AmsNetId);
    }
}
```

### Reconnection Tests

Test client recovery by starting/stopping the mock server:

```csharp
[Fact]
public async Task Client_ShouldReconnect_AfterServerRestart()
{
    // Arrange - Start mock server and connect client
    using var mockServer = new AdsServerMock();
    mockServer.RegisterSymbol("MyVar.Temperature", 25.0f);

    var client = CreateTwinCatClientSource(mockServer.ServerAddress);
    await client.StartAsync(CancellationToken.None);

    // Act - Kill server, wait, restart
    mockServer.Dispose();
    await Task.Delay(TimeSpan.FromSeconds(2));

    using var newServer = new AdsServerMock();
    newServer.RegisterSymbol("MyVar.Temperature", 30.0f);

    // Assert - Client should reconnect and receive new value
    await AsyncTestHelpers.WaitUntilAsync(
        () => client.Diagnostics.IsConnected,
        timeout: TimeSpan.FromSeconds(30),
        message: "Client should reconnect after server restart");
}

[Fact]
public async Task CircuitBreaker_ShouldOpen_AfterRepeatedFailures()
{
    // Arrange - Start mock server
    using var mockServer = new AdsServerMock();
    var client = CreateTwinCatClientSource(mockServer.ServerAddress);
    await client.StartAsync(CancellationToken.None);

    // Act - Kill server and let reconnection attempts fail
    mockServer.Dispose();
    await Task.Delay(TimeSpan.FromSeconds(10));  // Let circuit breaker trip

    // Assert - Circuit breaker should be open
    Assert.True(client.Diagnostics.IsCircuitBreakerOpen);
    Assert.True(client.Diagnostics.CircuitBreakerTripCount >= 1);
}

[Fact]
public async Task WriteRetryQueue_ShouldFlush_AfterReconnection()
{
    // Arrange - Connect client, then disconnect
    using var mockServer = new AdsServerMock();
    var writtenValues = new List<(string, object)>();
    mockServer.OnWrite = (symbol, value) => writtenValues.Add((symbol, value));

    var client = CreateTwinCatClientSource(mockServer.ServerAddress);
    await client.StartAsync(CancellationToken.None);

    mockServer.Dispose();

    // Act - Write while disconnected (should queue)
    subject.Temperature = 50.0f;

    // Restart server
    using var newServer = new AdsServerMock();
    newServer.OnWrite = (symbol, value) => writtenValues.Add((symbol, value));

    // Assert - Queued write should flush after reconnection
    await AsyncTestHelpers.WaitUntilAsync(
        () => writtenValues.Any(w => w.Item2.Equals(50.0f)),
        timeout: TimeSpan.FromSeconds(30),
        message: "Queued write should flush after reconnection");
}
```

The mock library can also replay recorded ADS communication from `.cap` files (TwinCAT ADS Monitor), useful for testing complex multi-step scenarios.

## Implementation Tasks

1. Create project `Namotion.Interceptor.Connectors.TwinCAT`
2. Add NuGet references (`Beckhoff.TwinCAT.Ads`, `Beckhoff.TwinCAT.Ads.Reactive`)
3. Implement `AdsClientConfiguration` with `Validate()` method
4. Implement `AdsValueConverter` with virtual methods for type conversion
5. Implement `AdsVariableAttribute` extending `PathAttribute`
6. Implement `AdsReadMode` enum
7. Implement `AdsErrorClassifier` for transient vs permanent error classification
8. Implement `AdsWriteException` (like `OpcUaWriteException`) with transient/permanent failure counts
9. Implement `AdsClientDiagnostics` for runtime health monitoring
10. Implement `AdsSubjectLoader`:
    - Recursive subject graph loading with cycle detection
    - Handle `IsSubjectReference`, `IsSubjectCollection`, `IsSubjectDictionary`
    - Array size discovery via TwinCAT symbol metadata
    - Path building for nested subjects, collections, dictionaries
11. Implement `TwinCatSubjectClientSource`:
    - Implement `ISubjectSource` interface
    - Add ADS connection management
    - Add notification registration via `WhenNotification`
    - Add polling registration via `PollValues`
    - Add Auto mode demotion logic (CycleTime-based priority)
    - Add cache invalidation on detach
    - Add reconnection handling in `ExecuteAsync`
    - Integrate existing `CircuitBreaker` from `Namotion.Interceptor.Connectors.Resilience`
    - Expose `Diagnostics` property
12. Implement `TwinCatSubjectExtensions` for DI registration
13. Write unit tests with `dsian.TwinCAT.Ads.Server.Mock`:
    - Value converter tests (all type mappings)
    - Error classifier tests
    - Read mode demotion logic
    - Subject graph loading (nested subjects, collections, dictionaries)
    - Path building for all property types
    - Full `ISubjectSource` flow with mocked ADS
    - Reconnection tests (server restart recovery)
    - Circuit breaker tests (open after failures, close after success)
    - Write retry queue flush after reconnection
    - Cache invalidation on detach
14. Write user-facing documentation (`docs/connectors/twincat.md`)
15. Add to `docs/connectors.md` index

## Open Questions

1. Does `PollValues` batch internally or poll each symbol individually? (Assumed individual based on API shape)
2. What are the exact ADS notification limits? (Runtime-dependent)
3. Should we support ADS sum commands for writes as well as reads?
