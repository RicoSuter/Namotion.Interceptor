using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Code-based OPC UA mapper produced by <see cref="OpcUaFluentMapperBuilder{TRoot}.Build"/>. Combines a
/// <see cref="FluentPathProvider"/>-backed node mapper with an <see cref="OpcUaFluentMetadataMapper"/>
/// (node and monitoring metadata, including the class-level type-self fallback). Compose it into an
/// <see cref="OpcUaCompositeMapper"/>, typically after the attribute mappers so fluent wins on conflicts.
/// </summary>
public sealed class OpcUaFluentMapper : ReverseCompositeMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    public OpcUaFluentMapper(FluentMappingRegistry<OpcUaPropertyMapping> registry, char pathSeparator = '.')
        : base(
            new OpcUaPathProviderMapper(new FluentPathProvider(registry, pathSeparator)),
            new OpcUaFluentMetadataMapper(registry))
    {
    }
}
