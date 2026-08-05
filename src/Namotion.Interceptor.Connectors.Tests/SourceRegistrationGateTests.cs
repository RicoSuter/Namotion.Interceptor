using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;

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

    [Fact]
    public async Task WhenWithSourceMonitoringIsUsedWithAHost_ThenTheHostsLoggerIsBridgedIntoTheContext()
    {
        // Arrange
        // No documented setup ever adds an ILoggerFactory to the context by hand, so without
        // bridging it here the monitor's lazy logger resolver (see WithSourceMonitoring()) always
        // returns null and every wait-engine warning is a silent no-op.
        var builder = Host.CreateApplicationBuilder();
        var recordingLogger = new RecordingLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RecordingLoggerProvider(recordingLogger));

        var context = InterceptorSubjectContext.Create().WithSourceMonitoring(builder.Services);
        var root = new Person(context);
        var host = builder.Build();

        // Act
        await host.StartAsync();
        try
        {
            // Registration is already complete (SourceRegistrationGate ran on host start), so this
            // wait's empty scope both completes vacuously and logs its diagnostic. The logger it
            // goes through must be the host's own DI-configured one for the assertion below to see
            // anything.
            await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await host.StopAsync();
        }

        // Assert
        Assert.Contains(recordingLogger.Warnings, message => message.Contains("has no in-scope source"));
    }
}

/// <summary>Always resolves to the same <see cref="RecordingLogger"/>, regardless of category.</summary>
internal sealed class RecordingLoggerProvider(RecordingLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
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
