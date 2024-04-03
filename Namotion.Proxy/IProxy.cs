using System.Collections.Concurrent;

namespace Namotion.Proxy;

public interface IProxy
{
    IProxyContext? Context { get; set; }

    ConcurrentDictionary<string, object?> Data { get; }

    IReadOnlyDictionary<string, ProxyPropertyInfo> Properties { get; }
}

public record struct ProxyPropertyInfo(
    string Name, // TODO: Remove as already defined as key in the dictionary
    Type Type,
    object[] Attributes,
    Func<object?, object?>? GetValue,
    Action<object?, object?>? SetValue)
{
    public readonly bool IsDerived => GetValue is not null && SetValue is null;
}
