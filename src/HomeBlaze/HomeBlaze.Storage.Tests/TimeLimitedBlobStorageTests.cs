using System.Collections.Concurrent;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public sealed class TimeLimitedBlobStorageTests : IDisposable
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();
    private readonly PausableBlobStorage _client = new(StorageFactory.Blobs.InMemory());
    private readonly TimeLimitedBlobStorage _storage;

    public TimeLimitedBlobStorageTests()
    {
        _storage = new TimeLimitedBlobStorage(_client, _timeProvider, _logger);
    }

    [Fact]
    public async Task WhenAbandonedCallIsStillBlocked_ThenNextCallFailsFastWithoutReachingTheClient()
    {
        // Arrange
        var reached = _client.BlockNext(nameof(IBlobStorage.ListAsync));
        var abandoned = _storage.ListAsync();
        await reached;
        await LetHangingCallTimeOutAsync();
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => abandoned);

        // Act
        var refused = _storage.ExistsAsync(["Notes.md"]);
        var refusedWasCompleted = refused.IsCompleted;
        var exception = await Record.ExceptionAsync(() => refused);
        var callCountWhileBlocked = _client.CallCount;
        _client.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !_storage.IsUnresponsive);
        var exists = await _storage.ExistsAsync(["Notes.md"]);

        // Assert
        Assert.True(refusedWasCompleted);
        Assert.IsType<StorageUnresponsiveException>(exception);
        Assert.Equal(1, callCountWhileBlocked);
        Assert.Equal(new[] { false }, exists);
        Assert.Equal(2, _client.CallCount);
    }

    [Fact]
    public async Task WhenAbandonedOpenReturnsLate_ThenItsStreamIsDisposed()
    {
        // Arrange
        using var content = new MemoryStream([1, 2, 3]);
        await _storage.WriteAsync("Notes.md", content);
        var reached = _client.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        var open = _storage.OpenReadAsync("Notes.md");
        await reached;
        await LetHangingCallTimeOutAsync();
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => open);

        // Act
        _client.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !_storage.IsUnresponsive);

        // Assert
        Assert.Equal(1, _client.OpenedStreamCount);
        Assert.Equal(1, _client.DisposedStreamCount);
    }

    [Fact]
    public async Task WhenAbandonedCallFailsLate_ThenItsFaultIsObserved()
    {
        // Arrange
        var reached = _client.PauseNext(nameof(IBlobStorage.ListAsync));
        var call = _storage.ListAsync();
        await reached;
        await LetHangingCallTimeOutAsync();
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => call);
        var failure = new IOException("The share is gone.");

        // Act
        _client.FailHangingCall(failure);
        await AsyncTestHelpers.WaitUntilAsync(() => !_storage.IsUnresponsive);

        // Assert
        Assert.Same(failure, Assert.Single(_logger.Exceptions));
    }

    [Fact]
    public async Task WhenCallerCancels_ThenCallIsCancelledWithTheCallersToken()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var reached = _client.PauseNext(nameof(IBlobStorage.ListAsync));
        var call = _storage.ListAsync(cancellationToken: cancellation.Token);
        await reached;

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => call);

        // Assert
        var cancelled = Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
    }

    public void Dispose() => _storage.Dispose();

    private async Task LetHangingCallTimeOutAsync()
    {
        // The call can arrive in the storage before its caller has started the wait that the limit ends.
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 1);
        _timeProvider.Advance(StorageCallTimeout.Limit);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IReadOnlyCollection<Exception> Exceptions => _exceptions;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception != null)
            {
                _exceptions.Enqueue(exception);
            }
        }
    }
}
