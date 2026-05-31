# Fluent mapping as a complete code-based alternative to attributes

Status: approved design, ready for implementation planning
Issue: [#325](https://github.com/RicoSuter/Namotion.Interceptor/issues/325)
Follow-up from: [#323](https://github.com/RicoSuter/Namotion.Interceptor/pull/323) (property-mapper abstraction)

## Goal

Make code-based fluent configuration a complete, standalone alternative to attribute mapping for the MQTT and OPC UA connectors, so an external or per-deployment configuration can map everything without annotating the subject types. Attributes keep working unchanged, and the two sources coexist through the existing composite mapper.

The current fluent mappers key by absolute instance path, which prevents two things attributes handle naturally:

1. Reuse across locations. A type used in several places (a `Motor` at `Pump.Motor` and at `Motors[2]`) must be redefined per location.
2. Collection and dictionary elements. Selectors are member-access chains, so element properties cannot be configured fluently at all.

## Non-goals

- No change to the canonical or wire path format. Index segments stay in bracket form (`Motors[2]`) everywhere (MQTT topics, OPC UA browse names, MCP browse paths, HomeBlaze canonical paths). Idiomatic slash topics for MQTT are a separate optional follow-up.
- No per-instance topic or segment override. Instance-level (`ForPath`) overrides are metadata-only.
- No fluent `[InlinePaths]` declaration. The attribute keeps working and composes with fluent segments; declaring a container as inline stays an attribute for now.
- No `ForPath` onto a subject (own-node config). That is covered at the type level by `ForType<T>().Map(...)` on the reference property plus `ForType<T>().Configure(...)`.

## Approach

Reuse the existing path engine and composite. Fluent stops being a separate mapping engine and becomes a second source, keyed the same way attributes are keyed, that feeds the same machinery. The connectors' path-provider mappers and the composite are reused verbatim.

The reason attributes already support reuse, collections, and cheap reverse lookup is that they key segments by `(declaringType, member)` and let `PathProviderBase` plus `PathExtensions` do the hierarchical composition. Today's fluent fails only because it keys by absolute instance path. The fix replaces the source, not the engine.

### The simplification that keeps complexity low

`ForPath` (instance-level) overrides are metadata-only and never change the path or topic. Segments stay a type-level concern (`ForType`). Consequences:

- The topic or browse name is always composed from type-level segments, so it is identical forward and reverse.
- Reverse lookup is owned entirely by the existing path-provider mappers. The fluent metadata mapper returns `null` on reverse, exactly like `MqttAttributeMapper` does today. No new reverse code, no instance reverse index, no O(n) subtree scans anywhere.

## Components

### Reused unchanged

`MqttPathProviderMapper`, `OpcUaPathProviderMapper`, `ReverseCompositeMapper` and the connector composites, the path engine (apart from the single `TryGetPath` fix below), `IPropertyMapping<T>.Merge`, and the `MqttPropertyMapping` / `OpcUaPropertyMapping` records.

### New, shared

- `IFluentSegmentSource` (Registry): `bool TryGetSegment(Type subjectType, string member, out string? segment)`. Small non-generic seam so Registry does not depend on connector metadata types.
- `FluentPathProvider : PathProviderBase` (Registry): a drop-in replacement for `AttributeBasedPathProvider`. Overrides `IsPropertyIncluded` (registered, or has `[InlinePaths]`) and `TryGetPropertySegment` (the registered segment, or the member `BrowseName` when no segment was given). Inherits `[InlinePaths]`, array handling, and segment-guided reverse for free. The segment is context-free per property, so there is no recursion against `TryGetPath`.
- `FluentMappingRegistry<TMetadata> : IFluentSegmentSource` (Connectors): the source of truth. Three maps:
  - `(Type, member) -> { segment, metadata }` (type-level)
  - `instancePath -> metadata` (instance-level, metadata only)
  - `Type -> metadata` (type-self, for `Configure`)
  Owns the inheritance walk (runtime type, then base classes, then interfaces, most-derived first, classes before interfaces).
- `FluentMetadataMapper<TMetadata, TKey>` (Connectors): forward resolution merges instance metadata over type-level metadata, most-specific-wins via the existing `Merge`. Reverse returns `null`. MQTT uses it directly.
- `FluentMappingBuilder<TRoot, TMetadata>` (Connectors): the `ForType` / `ForPath` API, root-scoped. Reuses `ExpressionPathHelper`.

### New, per-connector (small)

- A metadata property-builder mapping fluent calls to `TMetadata` plus segment. This reuses the field-setters of the existing builders (`WithQualityOfService` / `WithRetain`; `BrowseName` / `ReferenceType` / and so on), retargeted to write into the registry instead of a path-keyed dictionary.
- DI wiring in `MqttSubjectExtensions` / `OpcUaSubjectExtensions` (opt-in fluent configuration callback). The default wiring stays attributes-only.
- OPC UA only: `OpcUaFluentMetadataMapper` overrides the shared base to merge type-self metadata for subject-typed members (references, and collection or dictionary element types). This ports `OpcUaAttributeMapper`'s existing class-level fallback to the registry, no new concept.

### Deleted

- `MqttFluentMapper.cs` (old absolute-path mapper and O(n) reverse scan).
- `OpcUaFluentMapper.cs` (old, including the nested `PropertyBuilder` and `IPropertyBuilder.Map<TProperty>` recursion).
- The path-keying and nested-`Map` logic in both. The absolute-path coupling and both O(n) reverse scans are removed.

## Public API

The configuration is root-scoped (like today's `MqttFluentMapper<T>` / `OpcUaFluentMapper<T>`). `ForType<T>()` returns a type-scoped builder whose `Map` and `Configure` return that same builder; it also carries `ForType<U>()` and `ForPath(...)` so the chain flows into the next type. Structural chaining uses no statement-body lambdas; per-member configuration is a pipe-expression lambda.

### Two entry points

- `ForType<T>().Map(member, configure)`: type-level. The member selector is a single member (no chain, no indexer). The member may be a value leaf or a subject / reference property. Applies everywhere the type appears, including collection and dictionary elements, with inheritance.
- `ForType<T>().Configure(configure)`: class-level / type-self node config (the equivalent of `[OpcUaNode]` on a class). OPC UA only in practice; MQTT has no class-level config.
- `ForPath(selector, configure)`: instance-level, metadata-only. The selector is a full absolute path from the root and may contain indexers (`Motors[2].Speed`, `Sensors["boiler"].Value`). It must resolve to a value leaf; a subject, subject-collection, or subject-dictionary target throws at registration time.

### MQTT example

The MQTT builder uses `WithSegment` for the topic level, since an MQTT name has no other meaning.

```csharp
var fluent = new MqttFluentMapping<Plant>();

fluent
    .ForType<Motor>()
        .Map(m => m.Speed,  b => b.WithSegment("speed").WithQualityOfService(1))
        .Map(m => m.Torque, b => b.WithSegment("torque"))
    .ForType<Pump>()
        .Map(p => p.Motor,  b => b.WithSegment("motor"))
    .ForPath(p => p.Motors[2].Speed,            b => b.WithQualityOfService(2))
    .ForPath(p => p.Sensors["boiler"].Value,    b => b.WithRetain(true));
```

### OPC UA example

The OPC UA builder uses `BrowseName` as the path segment, since OPC UA browse paths are browse names. `BrowseName` sets both the segment (for composition and reverse navigation) and the node BrowseName. Omit it and both default to the member name, so most leaves need no `BrowseName` call.

```csharp
var fluent = new OpcUaFluentMapping<Plant>();

fluent
    .ForType<Motor>()
        .Configure(b => b.TypeDefinition("MotorType", "http://my/uri").NodeClass(OpcUaNodeClass.Object))
        .Map(m => m.Speed,  b => b.BrowseName("Speed").SamplingInterval(500))
        .Map(m => m.Torque, b => b.BrowseName("Torque").DataType("Double"))
    .ForType<Pump>()
        .Map(p => p.Motor,  b => b.BrowseName("Motor").ReferenceType("HasComponent"))
    .ForType<Plant>()
        .Map(p => p.Motors,  b => b.BrowseName("Motors").ItemReferenceType("HasComponent"))
        .Map(p => p.Sensors, b => b.BrowseName("Sensors").ItemReferenceType("HasComponent"))
    .ForType<Sensor>()
        .Configure(b => b.NodeClass(OpcUaNodeClass.Variable))
        .Map(s => s.Value, b => b.IsValue())
    .ForPath(p => p.Pump.Motor.Speed,        b => b.SamplingInterval(2000))
    .ForPath(p => p.Motors[0].Speed,         b => b.SamplingInterval(250).QueueSize(10))
    .ForPath(p => p.Sensors["boiler"].Value, b => b.DeadbandType(DeadbandType.Absolute).DeadbandValue(0.5));
```

The OPC UA metadata builder keeps the existing `IPropertyBuilder<T>` setters verbatim (`BrowseName`, `NodeIdentifier`, `TypeDefinition`, `DataType`, `ReferenceType`, `ItemReferenceType`, `SamplingInterval`, `QueueSize`, `DiscardOldest`, `DataChangeTrigger`, `DeadbandType`, `DeadbandValue`, `ModellingRule`, `EventNotifier`, `AdditionalReference`, and so on). Only `Map<TProperty>` (the nested recursion) is dropped, replaced by independent `ForType<TProperty>()`.

## ExpressionPathHelper

Two entry points:

- Strict single-member extraction for `ForType.Map`: exactly one member, throws on chain or indexer.
- Full-path-with-indexers extraction for `ForPath`: recognizes member chains, `ArrayIndex` binary nodes (arrays), and `get_Item(...)` method calls (`List`, `Dictionary`). Evaluates the index or key argument (constant or captured variable; it is a one-time config call, so compiling the sub-expression is acceptable) and emits the canonical bracket segment `Motors[2]` / `Sensors[boiler]`. The result matches the structural path that `TryGetPath` emits, so instance-registry keys and lookups agree.

`ForPath` also checks the selector leaf type and throws if it is a subject, subject-collection, or subject-dictionary.

## Lookup and precedence

Realized by composite order plus the instance-over-type merge inside the metadata mapper. No bespoke precedence engine.

```
[ pathProviderMapper(AttributeBasedPathProvider) , attributeMetadataMapper ,   // attributes (optional, for mixing)
  pathProviderMapper(FluentPathProvider)         , fluentMetadataMapper ]       // fluent, later wins
```

Effective ladder, most-specific-wins: instance fluent, then type-level fluent, then attributes. Pure code-based setups drop the attribute pair. The default wiring stays attributes-only and fluent is opt-in.

### Inheritance walk

A type-level registration is keyed by the type named in `ForType<T>()` plus the member name. At lookup, given a property's runtime holder type and member name, the registry checks `(candidate, member)` over the runtime type, then its base classes, then its interfaces, most-derived first, and uses the first hit. This makes `ForType<Motor>()` cover `ServoMotor` instances and an interface registration (`ForType<IMotor>()`) apply to concrete implementers, with the most specific registration winning.

## Single engine change

`PathExtensions.TryGetPath(string separator)` (the plain overload) currently drops indices and ignores `[InlinePaths]`. It is fixed to emit indices and handle `[InlinePaths]`, matching the provider overload, by sharing the frame-emit logic. Its only production callers are the two fluent mappers being deleted, so the blast radius is those plus tests. The index format stays brackets; no connector wire behavior changes.

## Coexistence and migration

Attributes and the code-based source merge via the existing composite, so adoption is incremental. Using the code-based source exclusively is opt-in. The fluent API is a breaking change relative to the deleted absolute-path mappers, which is acceptable at the current early development stage.

## Acceptance and parity checklist

- A type used in multiple locations and in collections or dictionaries is configured once in code and resolves everywhere, forward and reverse.
- Collection and array element properties resolve in both directions.
- Type-level plus instance-level (metadata-only) precedence implemented and tested.
- Polymorphic (base type and interface) registrations apply to derived and implementing types.
- Attributes continue to work and can be mixed with the code-based source via the composite.
- Shared implementation across MQTT and OPC UA, with no per-connector duplication of the keying, composition, or inheritance logic.
- `ForType<T>().Configure(...)` covers class-level node config so fluent is a complete attribute replacement (modulo the `[InlinePaths]` declaration marker).
- `ForPath` throws at registration when the target is not a value leaf.

## Testing and documentation (sketch, detailed in the plan)

- Parity tests for both connectors mirroring the attribute tests: reuse across locations, collection and dictionary elements forward and reverse, inheritance (base class and interface), instance metadata override, attribute plus fluent mixing.
- `ExpressionPathHelper` tests: single-member strictness, indexer parsing for array / `List` / `Dictionary`, leaf guard.
- `TryGetPath(string separator)` fix tests for indices and `[InlinePaths]`.
- Refresh public-API `.verified.txt` snapshots for changed assemblies.
- Update connector mapping docs (`docs/connectors-opcua-mapping.md`, `docs/connectors-mqtt.md`, `docs/connectors.md`).
