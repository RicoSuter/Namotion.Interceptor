namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Implemented by mapping records to enable composite merge semantics.
/// </summary>
public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    /// <summary>
    /// Merges two mappings: every non-null field of <paramref name="primary"/> wins, and where a field
    /// on <paramref name="primary"/> is null the value from <paramref name="fallback"/> is used. The
    /// caller decides which mapping is primary (see <see cref="ReverseCompositeMapper{TMapping,TKey}"/>,
    /// where the later mapper is primary so it wins).
    /// </summary>
    static abstract TSelf Merge(TSelf primary, TSelf fallback);
}
