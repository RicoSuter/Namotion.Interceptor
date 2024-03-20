namespace Namotion.Proxy;

public interface IProxyContext : IProxyContextProvider
{
    IProxyContext IProxyContextProvider.Context => this;

    object? GetProperty(object proxy, string propertyName, Func<object?> readValue);

    void SetProperty(object proxy, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
}
