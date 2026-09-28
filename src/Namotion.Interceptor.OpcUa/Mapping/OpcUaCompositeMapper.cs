using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Combines multiple OPC UA mappers: forward composition merges partial mappings ("last wins"),
/// reverse lookup tries mappers in reverse order and returns the first match.
/// </summary>
public sealed class OpcUaCompositeMapper(
    params IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] mappers)
    : ReverseCompositeMapper<OpcUaPropertyMapping, OpcUaLookupKey>(mappers);
