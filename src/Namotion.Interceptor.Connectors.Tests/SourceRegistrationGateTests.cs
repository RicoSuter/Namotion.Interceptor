using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Monitoring;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Coverage for the hosted-service path Getting Started leads with: WithSourceMonitoring(services)
/// registering a SourceRegistrationGate that completes registration once the host has started.
/// </summary>
public class SourceRegistrationGateTests
{
    [Fact]
    public async Task WhenApplicationStartedFires_ThenTheGateCompletesRegistration()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        using var lifetime = new FakeHostApplicationLifetime();
        var gate = new SourceRegistrationGate(context, lifetime);

        // Act
        await gate.StartAsync(CancellationToken.None);
        Assert.False(monitor.IsRegistrationComplete);
        lifetime.NotifyStarted();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenWithSourceMonitoringIsUsedWithAHost_ThenRegistrationCompletesOnceTheHostStarts()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext.Create().WithSourceMonitoring(builder.Services);
        var monitor = context.GetSourceMonitor();
        var host = builder.Build();

        // Act
        await host.StartAsync();
        try
        {
            // Assert - IHostApplicationLifetime.ApplicationStarted has already fired by the time
            // IHost.StartAsync returns, which is exactly what the registered SourceRegistrationGate
            // relies on to release the monitor's initial hold with no further signal needed.
            Assert.True(monitor.IsRegistrationComplete);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

/// <summary>
/// A minimal IHostApplicationLifetime whose ApplicationStarted token is controlled directly by the
/// test, instead of going through a full IHost start/stop cycle.
/// </summary>
internal sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void NotifyStarted() => _started.Cancel();

    public void StopApplication() => _stopping.Cancel();

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
