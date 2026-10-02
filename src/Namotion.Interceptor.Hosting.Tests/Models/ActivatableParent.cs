using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject with a hosted service and a child that has one too, so a test can see what happens to an
/// activation below the subject a host was started for.
/// </summary>
[InterceptorSubject]
public partial class ActivatableParent : ISubjectHostedServiceFactory
{
    public partial ActivatableSubject? Child { get; set; }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
}
