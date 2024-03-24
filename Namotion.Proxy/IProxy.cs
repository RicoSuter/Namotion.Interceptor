using System.Collections.Concurrent;

namespace Namotion.Proxy;

public interface IProxy
{
    IProxyContext? Context { get; set; }

    ConcurrentDictionary<string, object?> Data { get; }

    IReadOnlyDictionary<string, PropertyMetadata> Properties { get; }
}

public record struct PropertyMetadata(
    string PropertyName, // TODO: Remove as already defined as key in the dictionary
    System.Reflection.PropertyInfo Info,
    Func<object?, object?>? GetValue,
    Action<object?, object?>? SetValue)
{
    public readonly bool IsDerived => GetValue is not null && SetValue is null;
}
