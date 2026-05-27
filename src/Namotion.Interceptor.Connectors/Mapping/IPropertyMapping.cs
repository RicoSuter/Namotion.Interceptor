namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Implemented by mapping records to enable composite merge semantics.
/// </summary>
public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    /// <summary>
    /// Merges two mappings; later (primary) values override earlier (fallback) values, null fields fall through.
    /// </summary>
    static abstract TSelf Merge(TSelf primary, TSelf fallback);
}
