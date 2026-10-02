using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject with a hosted service that configures its private context, recording each call
/// and whether it was already in a graph with lifecycle when the call came.
/// </summary>
[InterceptorSubject]
public partial class ContextConfiguratorFactorySubject : ISubjectHostedServiceFactory, IPrivateContextConfigurator
{
    private int _configureContextCount;

    public partial string? Name { get; set; }

    /// <summary>Whether <see cref="IPrivateContextConfigurator.ConfigureContext"/> adds the registry.</summary>
    public bool AddsRegistry { get; set; }

    /// <summary>Whether <see cref="IPrivateContextConfigurator.ConfigureContext"/> adds hosting, which it must not.</summary>
    public bool AddsHosting { get; set; }

    public int ConfigureContextCount => Volatile.Read(ref _configureContextCount);

    /// <summary>Whether the subject was already in a graph with lifecycle when its context was configured.</summary>
    public bool? WasAttachedWhenContextWasConfigured { get; private set; }

    public ScriptedBackgroundService? LastService { get; private set; }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
    {
        var service = new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
        LastService = service;
        return service;
    }

    void IPrivateContextConfigurator.ConfigureContext(IInterceptorSubjectContext context)
    {
        Interlocked.Increment(ref _configureContextCount);
        WasAttachedWhenContextWasConfigured = ((IInterceptorSubject)this).Context.TryGetService<LifecycleInterceptor>() is not null;

        if (AddsRegistry)
        {
            context.WithRegistry();
        }

        if (AddsHosting)
        {
            context.WithHostedServices(new ServiceCollection());
        }
    }
}
