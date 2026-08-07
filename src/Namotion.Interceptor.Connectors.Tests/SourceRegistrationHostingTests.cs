using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Covers the interaction between source monitoring and the hosted-service attach path.
/// </summary>
/// <remarks>
/// This is the only place the two meet. Namotion.Interceptor.Hosting and
/// Namotion.Interceptor.Connectors are siblings that do not reference each other, so before this
/// file no test project referenced both and the combination was entirely uncovered - which is how
/// the defect these tests pin survived: the registration gate fires on ApplicationStarted, but a
/// source attached to the subject graph is started from a queue that ApplicationStarted does not
/// wait for, so registration completed while the source had not registered yet and a wait on that
/// branch completed vacuously against an unsynchronized tree.
/// </remarks>
public class SourceRegistrationHostingTests
{
    [Fact]
    public async Task WhenAnAttachedHostedServiceHasNotStartedYet_ThenRegistrationIsNotCompleteAtHostStart()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        using var gate = new GatedStartHostedService();

        // Attached before the host starts, so its start is queued and drains asynchronously while
        // the host's own startup runs to completion.
        root.AttachHostedService(gate);

        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert - ApplicationStarted has fired and released the gate's initial hold, but this
        // service is still sitting in StartAsync, so the hold taken when it was attached is still
        // outstanding and registration must not be complete.
        Assert.False(monitor.IsRegistrationComplete);

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        gate.ReleaseStart();

        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenAnAttachedHostedServiceStartThrows_ThenItsHoldIsStillReleased()
    {
        // Arrange
        // The hold is released in a finally, so a failing start cannot wedge every wait on the tree
        // forever. Without that, this is a permanent hang rather than a wrong answer.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        root.AttachHostedService(new ThrowingStartHostedService());

        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenNoHostedServiceIsAttached_ThenHostStartAloneCompletesRegistration()
    {
        // Arrange
        // Companion to the first test: proves the assertion there distinguishes the two cases,
        // rather than registration simply never completing at host start.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }
}

/// <summary>A hosted service whose StartAsync blocks until the test releases it.</summary>
internal sealed class GatedStartHostedService : IHostedService, IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);

    public void ReleaseStart() => _release.Set();

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _release.Wait(TimeSpan.FromSeconds(10), cancellationToken), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _release.Dispose();
}

/// <summary>A hosted service whose StartAsync always fails.</summary>
internal sealed class ThrowingStartHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("start failed");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
