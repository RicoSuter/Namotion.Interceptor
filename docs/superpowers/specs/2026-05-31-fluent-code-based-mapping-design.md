# Fluent mapping as a complete code-based alternative to attributes

Status: approved design, ready for implementation planning
Issue: [#325](https://github.com/RicoSuter/Namotion.Interceptor/issues/325)
Follow-up from: [#323](https://github.com/RicoSuter/Namotion.Interceptor/pull/323) (property-mapper abstraction)
Deferred follow-up: [#328](https://github.com/RicoSuter/Namotion.Interceptor/issues/328) (instance-level `ForPath` overrides)

## Goal

Make code-based fluent configuration a complete, standalone alternative to attribute mapping for the MQTT and OPC UA connectors, so an external or per-deployment configuration can map everything without annotating the subject types. Attributes keep working unchanged, and the two sources coexist through the existing composite mapper.

The current fluent mappers key by absolute instance path, which prevents two things attributes handle naturally:

1. Reuse across locations. A type used in several places (a `Motor` at `Pump.Motor` and at `Motors[2]`) must be redefined per location.
2. Collection and dictionary elements. Selectors are member-access chains, so element properties cannot be configured fluently at all.

## Scope of v1

v1 delivers **type-level** fluent configuration only (`ForType<T>().Map(...)` plus `ForType<T>().Configure(...)`). That is a complete replacement for attributes, because attributes are themselves type-level only.

Instance-level overrides (`ForPath`, configuring one specific location differently from the type-level default) are deferred to [#328](https://github.com/RicoSuter/Namotion.Interceptor/issues/328). The v1 design is deliberately structured so `ForPath` is a clean additive extension that changes nothing in the type-level design. See the Future extension section.

## Non-goals

- No change to the canonical or wire path format. Index segments stay in bracket form (`Motors[2]`) everywhere (MQTT topics, OPC UA browse names, MCP browse paths, HomeBlaze canonical paths). Idiomatic slash topics for MQTT are a separate optional follow-up.
- No instance-level (`ForPath`) overrides in v1. Tracked in [#328](https://github.com/RicoSuter/Namotion.Interceptor/issues/328).
- No fluent `[InlinePaths]` declaration. The attribute keeps working and composes with fluent segments; declaring a container as inline stays an attribute for now.

## Approach

Reuse the existing path engine and composite. Fluent stops being a separate mapping engine and becomes a second source, keyed the same way attributes are keyed, that feeds the same machinery. The connectors' path-provider mappers and the composite are reused verbatim.

The reason attributes already support reuse, collections, and cheap reverse lookup is that they key segments by `(declaringType, member)` and let `PathProviderBase` plus `PathExtensions` do the hierarchical composition. Today's fluent fails only because it keys by absolute instance path. The fix replaces the source, not the engine.

### Why v1 stays simple

Type-level resolution keys on `(type, member)` directly and never needs a structural path string. Reverse lookup (incoming topic or node to property) stays entirely in the reused path-provider mapper, because the `FluentPathProvider` answers segments context-free per `(type, member)`, identical forward and reverse. The fluent metadata mapper therefore returns `null` on reverse, exactly like `MqttAttributeMapper` does today. No instance reverse index, no per-topic O(n) scan, none of the absolute-path machinery the old fluent mappers carried.

This same property (segments are type-level and reverse-neutral) is what makes `ForPath` in #328 a clean additive layer rather than a redesign.

## Components

### Reused unchanged

`MqttPathProviderMapper`, `OpcUaPathProviderMapper`, `ReverseCompositeMapper` and the connector composites, the path engine, `IPropertyMapping<T>.Merge`, and the `MqttPropertyMapping` / `OpcUaPropertyMapping` records.

### New, shared

- `IFluentSegmentSource` (Registry): `bool TryGetSegment(Type subjectType, string member, out string? segment)`. Small non-generic seam so Registry does not depend on connector metadata types.
- `FluentPathProvider : PathProviderBase` (Registry): a drop-in replacement for `AttributeBasedPathProvider`. Overrides `IsPropertyIncluded` (registered, or has `[InlinePaths]`) and `TryGetPropertySegment` (the registered segment, or the member `BrowseName` when no segment was given). Inherits `[InlinePaths]`, array handling, and segment-guided reverse for free. The segment is context-free per property, so there is no recursion against `TryGetPath`.
- `FluentMappingRegistry<TMetadata> : IFluentSegmentSource` (Connectors): the source of truth. Two maps:
  - `(Type, member) -> { segment, metadata }` (type-level)
  - `Type -> metadata` (type-self, for `Configure`)
  Owns the inheritance walk (runtime type, then base classes, then interfaces, most-derived first, classes before interfaces). The structure leaves room for an `instancePath -> metadata` map to be added by #328 without restructuring.
- `FluentMetadataMapper<TMetadata, TKey>` (Connectors): forward resolution returns the type-level metadata for the property's `(type, member)`. Reverse returns `null`. MQTT uses it directly.
- `FluentMappingBuilder<TRoot, TMetadata>` (Connectors): the `ForType` API, root-scoped. Reuses `ExpressionPathHelper`.

### New, per-connector (small)

- A metadata property-builder mapping fluent calls to `TMetadata` plus segment. This reuses the field-setters of the existing builders (`WithQualityOfService` / `WithRetain`; `BrowseName` / `ReferenceType` / and so on), retargeted to write into the registry instead of a path-keyed dictionary.
- DI wiring in `MqttSubjectExtensions` / `OpcUaSubjectExtensions` (opt-in fluent configuration callback). The default wiring stays attributes-only.
- OPC UA only: `OpcUaFluentMetadataMapper` overrides the shared base to merge type-self metadata for subject-typed members (references, and collection or dictionary element types). This ports `OpcUaAttributeMapper`'s existing class-level fallback to the registry, no new concept.

### Deleted

- `MqttFluentMapper.cs` (old absolute-path mapper and O(n) reverse scan).
- `OpcUaFluentMapper.cs` (old, including the nested `PropertyBuilder` and `IPropertyBuilder.Map<TProperty>` recursion).
- The path-keying and nested-`Map` logic in both. The absolute-path coupling and both O(n) reverse scans are removed.

## Public API

The configuration is root-scoped (like today's `MqttFluentMapper<T>` / `OpcUaFluentMapper<T>`). `ForType<T>()` returns a type-scoped builder whose `Map` and `Configure` return that same builder; it also carries `ForType<U>()` so the chain flows into the next type. Structural chaining uses no statement-body lambdas; per-member configuration is a pipe-expression lambda.

### Entry points

- `ForType<T>().Map(member, configure)`: type-level. The member selector is a single member (no chain, no indexer). The member may be a value leaf or a subject / reference property. Applies everywhere the type appears, including collection and dictionary elements, with inheritance.
- `ForType<T>().Configure(configure)`: class-level / type-self node config (the equivalent of `[OpcUaNode]` on a class). OPC UA only in practice; MQTT has no class-level config.

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
    .ForType<Plant>()
        .Map(p => p.Motors,  b => b.WithSegment("motors"))
        .Map(p => p.Sensors, b => b.WithSegment("sensors"));
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
        .Map(s => s.Value, b => b.IsValue());
```

The OPC UA metadata builder keeps the existing `IPropertyBuilder<T>` setters verbatim (`BrowseName`, `NodeIdentifier`, `TypeDefinition`, `DataType`, `ReferenceType`, `ItemReferenceType`, `SamplingInterval`, `QueueSize`, `DiscardOldest`, `DataChangeTrigger`, `DeadbandType`, `DeadbandValue`, `ModellingRule`, `EventNotifier`, `AdditionalReference`, and so on). Only `Map<TProperty>` (the nested recursion) is dropped, replaced by independent `ForType<TProperty>()`.

## ExpressionPathHelper

`ForType.Map` uses strict single-member extraction: exactly one member rooted at the lambda parameter, throwing on a chain or an indexer. The current helper already walks member chains, so this adds a single-member validation. The full-path-with-indexers entry point needed by `ForPath` is deferred to #328.

## Lookup and precedence

Realized by composite order. No bespoke precedence engine.

```
[ pathProviderMapper(AttributeBasedPathProvider) , attributeMetadataMapper ,   // attributes (optional, for mixing)
  pathProviderMapper(FluentPathProvider)         , fluentMetadataMapper ]       // fluent, later wins
```

Effective ladder: type-level fluent, then attributes. Pure code-based setups drop the attribute pair. The default wiring stays attributes-only and fluent is opt-in.

### Inheritance walk

A type-level registration is keyed by the type named in `ForType<T>()` plus the member name. At lookup, given a property's runtime holder type and member name, the registry checks `(candidate, member)` over the runtime type, then its base classes, then its interfaces, most-derived first, and uses the first hit. This makes `ForType<Motor>()` cover `ServoMotor` instances and an interface registration (`ForType<IMotor>()`) apply to concrete implementers, with the most specific registration winning.

## TryGetPath cleanup

`PathExtensions.TryGetPath(string separator)` (the plain overload) currently drops indices and ignores `[InlinePaths]`, unlike the provider overload. After the old fluent mappers are deleted it has no production callers, but it is public API with a latent bug.

It is fixed as a standalone cleanup: de-duplicate the two overloads behind one frame-emit, and make the plain one index- and `[InlinePaths]`-aware. This is not functionally required by v1 (type-level resolution never builds a path string), but it is worthwhile hygiene while we are in this code, and it is the prerequisite for #328's instance-path lookup. The method is kept (a useful structural-path helper with a custom separator), not removed. The index format stays brackets; no connector wire behavior changes.

## Coexistence and migration

Attributes and the code-based source merge via the existing composite, so adoption is incremental. Using the code-based source exclusively is opt-in. The fluent API is a breaking change relative to the deleted absolute-path mappers, which is acceptable at the current early development stage.

## Acceptance and parity checklist

- A type used in multiple locations and in collections or dictionaries is configured once in code and resolves everywhere, forward and reverse.
- Collection and array element properties resolve in both directions.
- Polymorphic (base type and interface) registrations apply to derived and implementing types.
- Attributes continue to work and can be mixed with the code-based source via the composite.
- Shared implementation across MQTT and OPC UA, with no per-connector duplication of the keying, composition, or inheritance logic.
- `ForType<T>().Configure(...)` covers class-level node config so fluent is a complete attribute replacement (modulo the `[InlinePaths]` declaration marker).
- `PathExtensions.TryGetPath(string separator)` emits indices and handles `[InlinePaths]`.

## Future extension: instance-level overrides (#328)

`ForPath(selector, configure)` adds instance-specific overrides keyed by an absolute path from the root. Decided constraints, kept here so they are not lost:

- Metadata-only. Sets delivery and monitoring fields (MQTT QoS and Retain; OPC UA sampling, queue, deadband), never the path or topic segment. This keeps the segment identical forward and reverse, so reverse lookup stays in the path-provider mapper and the fluent metadata mapper still returns `null` on reverse.
- Leaf-only. The selector must resolve to a value leaf; a subject target throws at registration.

Additive implementation, touching nothing in the v1 type-level design:

1. Add an `instancePath -> metadata` map to `FluentMappingRegistry`.
2. Prepend an instance-first check in `FluentMetadataMapper.TryGetMapping` (instance metadata merged over type-level).
3. Add a full-path-with-indexers entry point to `ExpressionPathHelper` (member chains plus `ArrayIndex` and `get_Item` for arrays, `List`, and `Dictionary`, evaluating the index or key, emitting canonical bracket segments). Relies on the `TryGetPath` fix above.
4. Add `ForPath(...)` to the fluent builder.

Per-instance addressing overrides (renaming or retyping one OPC UA node, or a per-instance MQTT topic) are reverse-coupled and would need a separate instance reverse index; they are a candidate for a later extension beyond #328.

## Testing and documentation (sketch, detailed in the plan)

- Parity tests for both connectors mirroring the attribute tests: reuse across locations, collection and dictionary elements forward and reverse, inheritance (base class and interface), attribute plus fluent mixing.
- `ExpressionPathHelper` test: single-member strictness (rejects chain and indexer).
- `TryGetPath(string separator)` fix tests for indices and `[InlinePaths]`.
- Refresh public-API `.verified.txt` snapshots for changed assemblies.
- Update connector mapping docs (`docs/connectors-opcua-mapping.md`, `docs/connectors-mqtt.md`, `docs/connectors.md`).
