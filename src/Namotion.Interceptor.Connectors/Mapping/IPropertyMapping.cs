namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Implemented by mapping records to enable composite merge semantics.
/// </summary>
public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    /// <summary>
    /// Combines two mappings into one where <paramref name="primary"/> takes precedence over
    /// <paramref name="fallback"/> on field-level conflicts. See
    /// <see cref="ReverseCompositeMapper{TMapping,TKey}"/> for the composite case where the later mapper
    /// is primary.
    /// </summary>
    static abstract TSelf Merge(TSelf primary, TSelf fallback);
}
