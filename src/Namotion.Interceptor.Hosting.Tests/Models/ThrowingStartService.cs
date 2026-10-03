using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

public sealed class ThrowingStartService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("start failed");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
