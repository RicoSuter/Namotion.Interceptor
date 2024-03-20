using System.Collections.Concurrent;

namespace Namotion.Proxy;

public interface IProxy
{
    IProxyContext? Context { get; set; }

    ConcurrentDictionary<string, object?> Data { get; }

    IEnumerable<PropertyInfo> Properties { get; }
}

public record struct PropertyInfo(
    string PropertyName,
    bool IsDerived,
    Func<object?> ReadValue)
{
}
