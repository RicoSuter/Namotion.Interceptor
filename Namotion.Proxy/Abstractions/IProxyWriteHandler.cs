namespace Namotion.Proxy.Abstractions;

public interface IProxyWriteHandler : IProxyHandler
{
    void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next);
}

public record struct WriteProxyPropertyContext(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName,
    object? NewValue,
    Func<object?> GetValueBeforeWrite)
{
}
