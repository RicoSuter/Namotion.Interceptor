namespace Namotion.Interceptor.Connectors.Mapping;

public interface IPropertyMapping<TSelf> where TSelf : IPropertyMapping<TSelf>
{
    static abstract TSelf Merge(TSelf primary, TSelf fallback);
}
