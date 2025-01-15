using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public interface IProxyContext : IInterceptorCollection, IServiceProvider
{
}
