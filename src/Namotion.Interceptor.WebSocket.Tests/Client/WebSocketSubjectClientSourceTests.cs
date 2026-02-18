using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class WebSocketSubjectClientSourceTests
{
    private static WebSocketSubjectClientSource CreateSource()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var subject = new TestRoot(context);
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:59999/ws")
        };

        return new WebSocketSubjectClientSource(
            subject, configuration, NullLogger<WebSocketSubjectClientSource>.Instance);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ShouldNotThrow()
    {
        // Arrange
        var source = CreateSource();

        // Act
        await source.DisposeAsync();
        await source.DisposeAsync();

        // Assert — no exception thrown
    }

    [Fact]
    public async Task WriteChangesAsync_AfterDispose_ShouldReturnFailure()
    {
        // Arrange
        var source = CreateSource();
        await source.DisposeAsync();
        var changes = ReadOnlyMemory<SubjectPropertyChange>.Empty;

        // Act
        var result = await source.WriteChangesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.False(result.IsFullySuccessful);
    }

    [Fact]
    public void Constructor_WithNullSubject_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebSocketSubjectClientSource(null!, configuration, NullLogger<WebSocketSubjectClientSource>.Instance));
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var subject = new TestRoot(context);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebSocketSubjectClientSource(subject, null!, NullLogger<WebSocketSubjectClientSource>.Instance));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var subject = new TestRoot(context);
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new WebSocketSubjectClientSource(subject, configuration, null!));
    }

    [Fact]
    public void Constructor_WithInvalidConfiguration_ShouldThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var subject = new TestRoot(context);
        var configuration = new WebSocketClientConfiguration(); // Missing ServerUri

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new WebSocketSubjectClientSource(subject, configuration, NullLogger<WebSocketSubjectClientSource>.Instance));
    }
}
