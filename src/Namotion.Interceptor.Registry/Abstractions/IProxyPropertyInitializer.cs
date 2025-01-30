namespace Namotion.Interceptor.Registry.Abstractions;

public interface IProxyPropertyInitializer
{
    void InitializeProperty(RegisteredProxyProperty property, object? index);
}