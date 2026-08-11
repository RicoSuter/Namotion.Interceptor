using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
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
    public async Task WhenTheSourceIsDisposed_ThenTheFailureNamesNoChange()
    {
        // Arrange
        var source = CreateSource();
        var changes = new[] { CreateChange(source.RootSubject) };
        await source.DisposeAsync();

        // Act
        var result = await source.WriteChangesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Empty(result.FailedChanges);
    }

    [Fact]
    public async Task WhenTheSocketIsNotConnected_ThenTheFailureNamesNoChange()
    {
        // Arrange: a source that never connected, so nothing is sent and no change is answered for.
        var source = CreateSource();
        var changes = new[] { CreateChange(source.RootSubject) };

        // Act
        var result = await source.WriteChangesAsync(changes, CancellationToken.None);

        // Assert: naming no change is what tells the batching loop the call itself failed, so it stops
        // instead of spending a blocking send on every remaining batch of the same flush.
        Assert.NotNull(result.Error);
        Assert.Empty(result.FailedChanges);
    }

    private static SubjectPropertyChange CreateChange(IInterceptorSubject subject)
    {
        return SubjectPropertyChange.Create(
            new PropertyReference(subject, nameof(TestRoot.Name)),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            "old",
            "new");
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
