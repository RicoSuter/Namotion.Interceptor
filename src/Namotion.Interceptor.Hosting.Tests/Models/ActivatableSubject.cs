using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject with a hosted service, counting how often the service is created so a test can tell
/// "never activated" from "activated once" from "activated twice".
/// </summary>
[InterceptorSubject]
public partial class ActivatableSubject : ISubjectHostedServiceFactory
{
    private int _createCount;

    public partial string? Name { get; set; }

    public int CreateCount => Volatile.Read(ref _createCount);

    public Func<ActivatableSubject, IHostedService> ServiceFactory { get; set; } =
        _ => new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));

    public IHostedService? LastService { get; private set; }

    public IServiceProvider? LastServiceProvider { get; private set; }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
    {
        Interlocked.Increment(ref _createCount);
        LastServiceProvider = serviceProvider;
        var service = ServiceFactory(this);
        LastService = service;
        return service;
    }
}
