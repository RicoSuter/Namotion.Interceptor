using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketSubjectHandlerSequenceTests
{
    private static WebSocketSubjectHandler CreateHandler(TestRoot? root = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        root ??= new TestRoot(context);
        return new WebSocketSubjectHandler(
            root,
            new WebSocketServerConfiguration(),
            NullLogger.Instance);
    }

    [Fact]
    public void CurrentSequence_ShouldStartAtZero()
    {
        var handler = CreateHandler();

        Assert.Equal(0L, handler.CurrentSequence);
    }

    [Fact]
    public async Task BroadcastChanges_WithNoConnections_ShouldNotIncrementSequence()
    {
        var handler = CreateHandler();

        // BroadcastChangesAsync short-circuits when no connections exist
        await handler.BroadcastChangesAsync(ReadOnlyMemory<Namotion.Interceptor.Tracking.Change.SubjectPropertyChange>.Empty, CancellationToken.None);

        Assert.Equal(0L, handler.CurrentSequence);
    }

    [Fact]
    public async Task RunHeartbeatLoopAsync_WithZeroInterval_ShouldReturnImmediately()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new TestRoot(context);
        var handler = new WebSocketSubjectHandler(
            root,
            new WebSocketServerConfiguration { HeartbeatInterval = TimeSpan.Zero },
            NullLogger.Instance);

        // Should return immediately without blocking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await handler.RunHeartbeatLoopAsync(cts.Token);

        // If we get here, the method returned immediately (heartbeat disabled)
    }

    [Fact]
    public async Task RunHeartbeatLoopAsync_ShouldRespectCancellation()
    {
        var handler = CreateHandler();

        using var cts = new CancellationTokenSource();
        var task = handler.RunHeartbeatLoopAsync(cts.Token);

        // Cancel quickly
        await cts.CancelAsync();

        // Should complete without throwing
        await task;
    }
}
