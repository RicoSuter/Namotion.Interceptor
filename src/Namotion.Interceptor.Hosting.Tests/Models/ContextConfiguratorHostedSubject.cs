using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is itself a hosted service and adds the registry to the context it runs in alone,
/// recording each call and whether it was already in a graph with lifecycle when the call came.
/// </summary>
[InterceptorSubject]
public partial class ContextConfiguratorHostedSubject : IHostedService, IPrivateContextConfigurator
{
    private int _configureContextCount;
    private int _startCount;

    public partial string? Name { get; set; }

    /// <summary>Whether <see cref="IPrivateContextConfigurator.ConfigureContext"/> adds hosting, which it must not.</summary>
    public bool AddsHosting { get; set; }

    public int ConfigureContextCount => Volatile.Read(ref _configureContextCount);

    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>Whether the subject was already in a graph with lifecycle when its context was configured.</summary>
    public bool? WasAttachedWhenContextWasConfigured { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void IPrivateContextConfigurator.ConfigureContext(IInterceptorSubjectContext context)
    {
        Interlocked.Increment(ref _configureContextCount);
        WasAttachedWhenContextWasConfigured = ((IInterceptorSubject)this).Context.TryGetService<LifecycleInterceptor>() is not null;
        context.WithRegistry();

        if (AddsHosting)
        {
            context.WithHostedServices(new ServiceCollection());
        }
    }
}
