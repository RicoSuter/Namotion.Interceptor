using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is a hosted service itself and has one as well, so a test can see both starts.
/// </summary>
[InterceptorSubject]
public partial class HostedActivatableSubject : IHostedService, ISubjectHostedServiceFactory
{
    private int _startCount;
    private int _createCount;

    public partial string? Name { get; set; }

    /// <summary>When set, the subject's own start throws it.</summary>
    public Exception? StartFault { get; set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public int CreateCount => Volatile.Read(ref _createCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return StartFault is { } fault ? Task.FromException(fault) : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
    {
        Interlocked.Increment(ref _createCount);
        return new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
    }
}
