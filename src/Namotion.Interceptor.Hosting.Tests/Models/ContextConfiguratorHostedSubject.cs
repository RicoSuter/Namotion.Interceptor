using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is itself a hosted service, not a factory, and adds the registry to the context it
/// runs in alone.
/// </summary>
[InterceptorSubject]
public partial class ContextConfiguratorHostedSubject : IHostedService, IPrivateContextConfigurator
{
    private int _configureContextCount;
    private int _startCount;

    public partial string? Name { get; set; }

    public int ConfigureContextCount => Volatile.Read(ref _configureContextCount);

    public int StartCount => Volatile.Read(ref _startCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void IPrivateContextConfigurator.ConfigureContext(IInterceptorSubjectContext context)
    {
        Interlocked.Increment(ref _configureContextCount);
        context.WithRegistry();
    }
}
