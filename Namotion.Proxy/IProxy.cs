using System.Collections.Concurrent;

namespace Namotion.Proxy;

public interface IProxy
{
    IProxyContext? Context { get; set; }

    ConcurrentDictionary<string, object?> Data { get; }

    IReadOnlyDictionary<string, PropertyInfo> Properties { get; }
}

public record struct PropertyInfo(
    string PropertyName, // TODO: Remove as already defined as key in the dictionary
    System.Reflection.PropertyInfo Info,
    bool IsDerived,
    Func<object?, object?> GetValue)
{
}
