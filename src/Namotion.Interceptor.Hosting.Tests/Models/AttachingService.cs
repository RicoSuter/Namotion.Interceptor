using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A service that attaches a child service to its subject from StartAsync, the way a device attaches
/// its own connector, so a test can see whether the child runs without a host.
/// </summary>
public sealed class AttachingService : IHostedService
{
    private readonly IInterceptorSubject _subject;

    public AttachingService(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public ScriptedBackgroundService? Child { get; private set; }

    public IHostedServiceAttachment? ChildAttachment { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ChildAttachment = _subject.AttachHostedService(() =>
            Child = new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
