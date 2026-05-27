# Property Mapper Abstraction Design

Date: 2026-05-27
Status: Design (not yet implemented)

## Context

Connectors translate properties on the subject graph into external-system representations: ADS symbol paths, MQTT topics, OPC UA node IDs, etc. Today, four mechanisms coexist for that translation, and they are inconsistent:

1. **`IPathProvider`** (in `Namotion.Interceptor.Registry/Paths/`). A three-method interface returning per-property segments plus an inclusion filter. Used by TwinCAT ADS, MQTT, WebSocket (for filtering only), and as one input source to OPC UA's mapper.
2. **`IOpcUaNodeMapper`** (in `Namotion.Interceptor.OpcUa/Mapping/`). A connector-specific richer interface returning `OpcUaNodeConfiguration` (30+ fields) plus an async reverse lookup. Four concrete implementations (`Attribute`, `PathProvider`, `Fluent`, `Composite`).
3. **Direct attribute reads.** `AdsSubscriptionManager` reaches into `[AdsVariable]` attributes for `ReadMode`, `CycleTime`, `MaxDelay`, `Priority`. The path comes from `IPathProvider`, but the rest of the per-property knobs are read ad-hoc.
4. **Global configuration only.** MQTT QoS and retain, WebSocket batch size, etc. have no per-property override at all.

The asymmetry produces several concrete pain points:

- TwinCAT users cannot supply absolute symbol paths for nested properties because `IPathProvider` only contributes path *segments*; the loader mechanically concatenates parent paths.
- MQTT cannot vary QoS or retain per topic.
- WebSocket has no way to add per-property batch overrides without a config redesign.
- The OPC UA fluent mapper pattern is great, but it is locked inside the OPC UA assembly.
- `AdsSubscriptionManager` reading attributes directly is hostile to non-attribute configuration (no fluent equivalent for ADS).

## Goals

1. **Unify connector property-to-external-system mapping** under one abstraction that lives in `Namotion.Interceptor.Connectors` and is reused by every connector that needs per-property metadata.
2. **Enable user-facing extensibility for all connectors** with the same patterns OPC UA has today: attributes, path provider adapter, fluent code-based config, composite merging.
3. **Make per-property metadata expressive** so each connector can grow its set of per-property knobs without an API redesign. Unlock the absolute-path use case in ADS, per-topic QoS in MQTT, etc.

## Non-goals

- Replacing `IPathProvider` in registry path utilities, MCP server, AspNetCore JSON path resolution, GraphQL, or HomeBlaze custom providers. These solve a different problem (graph-coordinate addressing) and would gain nothing from per-connector richness.
- Source generation for record `Merge` methods. Hand-written merges are small and readable.
- Adding WebSocket per-property fields today (the record starts empty and grows later).

## Architecture

### Layering

The new abstraction is **additive** alongside `IPathProvider`. Connectors stop consuming `IPathProvider` directly; they consume a new `IPropertyMapper<TMapping>`. The path-provider story for connectors becomes "one shipped implementation" via a per-connector adapter, exactly mirroring what `PathProviderOpcUaNodeMapper` does today.

| Consumer | Mapping abstraction |
|---|---|
| TwinCAT ADS | `IPropertyMapper<AdsPropertyMapping>` |
| MQTT (client and server) | `IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>` |
| OPC UA (client and server) | `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` |
| WebSocket (client and server) | `IPropertyMapper<WebSocketPropertyMapping>` |
| Registry path utilities (`PathExtensions`) | `IPathProvider` (unchanged) |
| MCP server tools | `IPathProvider` (unchanged) |
| AspNetCore JSON paths | own `SubjectRegistryJsonExtensions` (unchanged) |
| HomeBlaze custom path providers | `IPathProvider` (unchanged) |

### Core interfaces

Located in `Namotion.Interceptor.Connectors`.

```csharp
public interface IPropertyMapper<TMapping>
{
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping);
}

public interface IReversePropertyMapper<TMapping, TKey> : IPropertyMapper<TMapping>
{
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject,
        TKey key,
        CancellationToken cancellationToken);
}

public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    /// <summary>
    /// Merge an override mapping on top of a fallback. Override fields take precedence;
    /// null fields fall through to the fallback.
    /// </summary>
    static abstract TSelf Merge(TSelf over, TSelf fallback);
}
```

Design notes:

- **`TryGet…` naming** matches existing registry conventions (`TryGetPath`, `TryGetPropertyFromPath`, `TryGetPropertySegment`).
- **`out` parameter with `[NotNullWhen(true)]`** works for both reference and struct mappings; cleaner consumer side than nullable-return.
- **`ValueTask`** for reverse lookup avoids allocation when the reverse is synchronous (MQTT topic to property), while still permitting async (OPC UA session-side namespace resolution).
- **`TKey` as a record struct** per connector. Bundles identifier and context (e.g., OPC UA needs `ReferenceDescription + ISession`). Adding fields later is non-breaking when they have default values. Always declared `readonly record struct` to keep it allocation-free.
- **Reverse extends forward**: the same mapper implementation handles both directions and naturally shares state. There is no realistic case where a property is reverse-lookupable but not forward-mappable.
- **`IPropertyMapping<TSelf>`** is a behavioral contract (the static `Merge`), not a structural one. Mapping records have nothing in common at the field level, so there is no shared base record or instance interface.
- **Parameter order `Merge(over, fallback)`** matches OPC UA's existing `WithFallback(result)` convention, the `??` operator's left-is-preferred convention, and the body expression `over.X ?? fallback.X`.

### Generic primitives

Located in `Namotion.Interceptor.Connectors`.

```csharp
public class DelegatePropertyMapper<TMapping> : IPropertyMapper<TMapping>
{
    public DelegatePropertyMapper(Func<RegisteredSubjectProperty, TMapping?> selector);
    public bool TryGetMapping(RegisteredSubjectProperty property, out TMapping? mapping);
}

public class CompositePropertyMapper<TMapping> : IPropertyMapper<TMapping>
    where TMapping : IPropertyMapping<TMapping>
{
    public CompositePropertyMapper(params IPropertyMapper<TMapping>[] mappers)
        : this(TMapping.Merge, mappers) { }

    public CompositePropertyMapper(
        Func<TMapping, TMapping, TMapping> merge,
        params IPropertyMapper<TMapping>[] mappers);

    public bool TryGetMapping(RegisteredSubjectProperty property, out TMapping? mapping);
}

public abstract class FluentPropertyMapperBase<TSubject, TMapping> : IPropertyMapper<TMapping>
{
    // Storage and lookup mechanics (keyed by property-path-from-root).
    // Per-connector subclasses add type-safe builder methods.
}
```

Iteration order in the composite: mappers run left-to-right. Each non-null result is merged on top of the accumulated mapping as the override. Final result is null only if every inner mapper returns null.

### Per-connector pieces

Located in each connector library. The TwinCAT version is shown; MQTT, OPC UA, and WebSocket follow the same pattern.

```csharp
// 1. The mapping record
public sealed record AdsPropertyMapping(
    string? SymbolPath,
    AdsReadMode? ReadMode,
    int? CycleTime,
    int? MaxDelay,
    int? Priority)
    : IPropertyMapping<AdsPropertyMapping>
{
    public static AdsPropertyMapping Merge(AdsPropertyMapping over, AdsPropertyMapping fallback) => new(
        SymbolPath: over.SymbolPath ?? fallback.SymbolPath,
        ReadMode:   over.ReadMode   ?? fallback.ReadMode,
        CycleTime:  over.CycleTime  ?? fallback.CycleTime,
        MaxDelay:   over.MaxDelay   ?? fallback.MaxDelay,
        Priority:   over.Priority   ?? fallback.Priority);
}

// 2. The attribute (existing, with a ToMapping helper added)
public class AdsVariableAttribute : PathAttribute
{
    // existing fields...
    public AdsPropertyMapping ToMapping() => new(
        SymbolPath: SymbolPath,
        ReadMode: ReadMode == AdsReadMode.Auto ? null : ReadMode,
        CycleTime: CycleTime == int.MinValue ? null : CycleTime,
        MaxDelay: MaxDelay == int.MinValue ? null : MaxDelay,
        Priority: Priority == 0 ? null : Priority);
}

// 3. Attribute mapper (thin wrapper over DelegatePropertyMapper)
public class AdsAttributePropertyMapper : DelegatePropertyMapper<AdsPropertyMapping>
{
    public AdsAttributePropertyMapper(string? connectorName = null)
        : base(property => GetMappingFromAttribute(
            property, connectorName ?? AdsConstants.DefaultConnectorName)) { }

    private static AdsPropertyMapping? GetMappingFromAttribute(
        RegisteredSubjectProperty property, string connectorName)
    {
        var attribute = property.ReflectionAttributes
            .OfType<AdsVariableAttribute>()
            .FirstOrDefault(a => a.Name == connectorName);
        return attribute?.ToMapping();
    }
}

// 4. Path-provider adapter (thin wrapper over DelegatePropertyMapper)
public class AdsPathProviderPropertyMapper : DelegatePropertyMapper<AdsPropertyMapping>
{
    public AdsPathProviderPropertyMapper(IPathProvider pathProvider)
        : base(property => GetMappingFromPathProvider(property, pathProvider)) { }

    private static AdsPropertyMapping? GetMappingFromPathProvider(
        RegisteredSubjectProperty property, IPathProvider pathProvider)
    {
        if (!pathProvider.IsPropertyIncluded(property))
            return null;
        // Compose the symbol path via the existing hierarchical loader walk.
        // Returns a mapping with only SymbolPath set; other fields fall through
        // to whatever a later mapper (e.g. AdsAttributePropertyMapper) supplies.
        var path = property.TryGetPath(pathProvider, rootSubject: null);
        return path is null ? null : new AdsPropertyMapping(SymbolPath: path, null, null, null, null);
    }
}

// 5. Fluent mapper (subclass of generic base; adds type-safe builder methods)
public class AdsFluentPropertyMapper<TSubject> : FluentPropertyMapperBase<TSubject, AdsPropertyMapping>
{
    public AdsFluentMappingBuilder Map<TValue>(Expression<Func<TSubject, TValue>> property);
}

public class AdsFluentMappingBuilder
{
    public AdsFluentMappingBuilder WithSymbolPath(string symbolPath);
    public AdsFluentMappingBuilder WithCycleTime(int milliseconds);
    public AdsFluentMappingBuilder WithReadMode(AdsReadMode mode);
    public AdsFluentMappingBuilder WithPriority(int priority);
    // etc.
}
```

The optional per-connector helper class provides discoverability sugar but is not required:

```csharp
public static class AdsPropertyMappers
{
    public static IPropertyMapper<AdsPropertyMapping> Attributes(string? connectorName = null)
        => new AdsAttributePropertyMapper(connectorName);

    public static IPropertyMapper<AdsPropertyMapping> PathProvider(IPathProvider provider)
        => new AdsPathProviderPropertyMapper(provider);

    public static IPropertyMapper<AdsPropertyMapping> Composite(
        params IPropertyMapper<AdsPropertyMapping>[] mappers)
        => new CompositePropertyMapper<AdsPropertyMapping>(mappers);
}
```

### Reverse mapper details

MQTT and OPC UA need reverse lookup. For each, the connector defines a lookup key as a `readonly record struct`:

```csharp
// MQTT
public readonly record struct MqttLookupKey(string Topic);
// later when needed: (string Topic, IReadOnlyList<MqttUserProperty>? UserProperties = null)

// OPC UA
public readonly record struct OpcUaLookupKey(ReferenceDescription Reference, ISession Session);
```

The path-provider adapter for connectors that need reverse also implements `IReversePropertyMapper`:

```csharp
public class MqttPathProviderPropertyMapper
    : DelegatePropertyMapper<MqttPropertyMapping>,
      IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    public MqttPathProviderPropertyMapper(IPathProvider pathProvider, string? topicPrefix = null);

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject rootSubject, MqttLookupKey key, CancellationToken ct)
    {
        var path = topicPrefix is null ? key.Topic : StripPrefix(key.Topic, topicPrefix);
        var property = rootSubject.Subject.TryGetPropertyFromPath(path, _pathProvider);
        return new ValueTask<RegisteredSubjectProperty?>(property);
    }
}
```

OPC UA's path-provider adapter returns `null` from `TryGetPropertyAsync` (same as today's `PathProviderOpcUaNodeMapper`) because path-provider-derived browse names don't carry the namespace information needed for OPC UA reverse lookup. The attribute and fluent mappers provide reverse on their own.

## Configuration changes (hard break)

The project is at version 0.0.2 ("early development"). The change is a clean break with no compatibility shims.

### Configuration class

```csharp
public class AdsClientConfiguration
{
    private static readonly IPropertyMapper<AdsPropertyMapping> DefaultMapper =
        new CompositePropertyMapper<AdsPropertyMapping>(
            new AdsPathProviderPropertyMapper(
                new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName)),
            new AdsAttributePropertyMapper());

    public IPropertyMapper<AdsPropertyMapping> Mapper { get; set; } = DefaultMapper;

    public required string Host { get; set; }
    // ... rest unchanged
}
```

The default is a static composite instance shared across all configurations. Order: path provider first (fallback), attribute mapper second (override). This matches the existing OPC UA defaults in `OpcUaClientConfiguration.cs:11-13` and `OpcUaServerConfiguration.cs`. Same shape replicated in `MqttClientConfiguration`, `MqttServerConfiguration`, `OpcUaClientConfiguration` (rename), `OpcUaServerConfiguration` (rename), `WebSocketClientConfiguration`, and `WebSocketServerConfiguration`.

The previous `PathProvider` property is removed.

### DI extension methods

The simple overload exists for the zero-config case; anything beyond defaults goes through the full configuration object.

```csharp
// Simple — one PLC, default attribute+path-provider composite
services.AddTwinCatSubjectClientSource<MyPlc>(host: "192.168.1.100", amsPort: 851);

// Full configuration — anything custom (fluent mapping, alternate connector name, etc.)
services.AddTwinCatSubjectClientSource(
    sp => sp.GetRequiredService<MyPlc>(),
    _ => new AdsClientConfiguration {
        Host = "192.168.1.100",
        AmsPort = 851,
        Mapper = new CompositePropertyMapper<AdsPropertyMapping>(
            new AdsAttributePropertyMapper(),
            new AdsFluentPropertyMapper<MyPlc>()
                .Map(x => x.Motor.Speed).WithSymbolPath("GVL.Motors[0].Speed").WithCycleTime(50)),
    });
```

The previous `pathProviderName` parameter on the simple overload goes away. The connector-name concept survives (filtering `[Path("ads", ...)]` attributes by name in multi-protocol models) but moves into the mapper constructor:

```csharp
new AdsAttributePropertyMapper(connectorName: "production")
```

## Per-connector migration matrix

| Connector | What changes | Status |
|---|---|---|
| TwinCAT ADS | New: `AdsPropertyMapping`, `AdsAttributePropertyMapper`, `AdsPathProviderPropertyMapper`, `AdsFluentPropertyMapper<T>`. Update `AdsSubjectLoader` to use `IPropertyMapper<AdsPropertyMapping>` instead of `IPathProvider`. Update `AdsSubscriptionManager` to read per-property knobs from the mapping instead of directly from `[AdsVariable]`. | New |
| MQTT | New: `MqttPropertyMapping(string? Topic, MqttQualityOfServiceLevel? QoS, bool? Retain)`, attribute/path-provider/fluent mappers, reverse implementation. Update `MqttSubjectClientSource` and `MqttSubjectServer` to use the mapper. Per-topic QoS and retain become usable. | New |
| OPC UA | Rename `IOpcUaNodeMapper` to `IPropertyMapper<OpcUaPropertyMapping>` (with reverse). Rename `OpcUaNodeConfiguration` record to `OpcUaPropertyMapping`. The four existing concrete mappers (`AttributeOpcUaNodeMapper`, `PathProviderOpcUaNodeMapper`, `FluentOpcUaNodeMapper`, `CompositeNodeMapper`) become per-connector wrappers or the generic primitives. `OpcUaLookupKey` record struct introduced. | Rename and consolidate |
| WebSocket | New: empty `WebSocketPropertyMapping` record implementing `IPropertyMapping<WebSocketPropertyMapping>` (no fields today, no merge logic beyond returning the override). Update `WebSocketSubjectClientSource` and `WebSocketSubjectServer` to use the mapper for inclusion checks instead of `IPathProvider.IsPropertyIncluded`. Future per-property fields added as needed. | New |

## Worked examples

### Default behavior, no configuration

```csharp
[InterceptorSubject]
public partial class Motor
{
    [AdsVariable("GVL.Motor.Speed")]
    public partial double Speed { get; set; }
}

services.AddTwinCatSubjectClientSource<Motor>(host: "192.168.1.100");
```

The default mapper is a composite of path-provider plus attribute. `Speed` has `[AdsVariable]`, so the attribute mapper returns `AdsPropertyMapping(SymbolPath: "GVL.Motor.Speed", ReadMode: null, ...)`. The path-provider mapper also returns a partial mapping (just the path). Composite merge: attribute overrides path-provider where set; result is the attribute's mapping.

### Absolute paths via fluent mapping (the original motivating case)

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial double Speed { get; set; }
    public partial double Torque { get; set; }
}

[InterceptorSubject]
public partial class Plant
{
    public partial Motor Motor1 { get; set; }
    public partial Motor Motor2 { get; set; }
}

services.AddTwinCatSubjectClientSource(
    sp => sp.GetRequiredService<Plant>(),
    _ => new AdsClientConfiguration {
        Host = "192.168.1.100",
        Mapper = new AdsFluentPropertyMapper<Plant>()
            .Map(p => p.Motor1.Speed).WithSymbolPath("PRODUCTION.LINE1.M1.Speed").WithCycleTime(50)
            .Map(p => p.Motor1.Torque).WithSymbolPath("PRODUCTION.LINE1.M1.Torque")
            .Map(p => p.Motor2.Speed).WithSymbolPath("PRODUCTION.LINE1.M2.Speed").WithCycleTime(50)
            .Map(p => p.Motor2.Torque).WithSymbolPath("PRODUCTION.LINE1.M2.Torque"),
    });
```

The fluent mapper returns absolute symbol paths per leaf property. The subject loader stops walking the hierarchy concatenating parent segments because the mapping already supplies the full path. Each leaf maps to its declared PLC symbol regardless of nesting.

### Per-topic MQTT QoS

```csharp
services.AddMqttSubjectClientSource(
    sp => sp.GetRequiredService<Sensors>(),
    _ => new MqttClientConfiguration {
        BrokerHost = "broker.example.com",
        Mapper = new CompositePropertyMapper<MqttPropertyMapping>(
            new MqttAttributePropertyMapper(),
            new MqttFluentPropertyMapper<Sensors>()
                .Map(s => s.CriticalTemp)
                    .WithTopic("alerts/critical/temp")
                    .WithQoS(MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithRetain(true),
                .Map(s => s.RoutineLog).WithQoS(MqttQualityOfServiceLevel.AtMostOnce)),
    });
```

Per-topic QoS and retain are unavailable today; the new abstraction enables them with no further changes to MQTT internals beyond reading from the mapping.

## Out of scope

- WebSocket per-property fields. The record starts empty and grows when a real use case appears.
- Replacing `IPathProvider` in registry path utilities, MCP server, AspNetCore JSON paths, GraphQL, or HomeBlaze. These solve graph-coordinate addressing, not connector configuration.
- Source generator for `Merge`. The hand-written merge per record is small (~6 lines) and readable.
- A builder-pattern `.AddMapper(...)` extension that mutates a composite default in place. Setting `Mapper = ...` to replace the default is sufficient and unsurprising.
- Migration deprecation period. Hard break is appropriate for pre-1.0.

## Open questions for implementation

- Whether the fluent mapper's per-property dictionary should key by `PropertyInfo`, by property-path-from-root-string, or by a tuple. OPC UA's `FluentOpcUaNodeMapper` keys by path string; matching that simplifies the implementation but loses some compile-time safety.
- Whether `AdsSubjectLoader` should be opened up via an interface (`IAdsSubjectLoader`) for users who want to bypass the path-from-mapping-segment walk entirely. The fluent mapping use case (absolute paths) needs the loader to recognize that a leaf mapping with a full `SymbolPath` should not have parent segments prepended. Two possible approaches:
  1. The loader treats any non-null `SymbolPath` returned by the mapper as the complete path, never prepending parent segments.
  2. A separate flag in the mapping (`bool IsAbsolutePath`) signals "use this path as-is."
  Option 1 is simpler; the fallback path-provider mapper's "partial path" semantics still work because the path it produces already includes parent segments (computed via `TryGetPath`). To be confirmed during implementation.
- Whether existing tests for `IOpcUaNodeMapper` migrate verbatim under a renamed type, or whether some can collapse onto the generic `CompositePropertyMapper` tests in the Connectors package.
