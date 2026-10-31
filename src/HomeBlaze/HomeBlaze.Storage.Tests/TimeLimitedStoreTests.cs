using System.Collections.Concurrent;
using FluentStorage;
using FluentStorage.Model;
using FluentStorage.Storage;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public sealed class TimeLimitedStoreTests : IDisposable
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();
    private readonly PausableStore _client = new(StorageFactory.InMemory());
    private readonly TimeLimitedStore _storage;

    public TimeLimitedStoreTests()
    {
        _storage = new TimeLimitedStore(_client, _timeProvider, _logger);
    }

    [Fact]
    public async Task WhenAbandonedCallIsStillBlocked_ThenNextCallFailsFastWithoutReachingTheClient()
    {
        // Arrange
        var reached = _client.BlockNext(nameof(IStore.ListObjects));
        var abandoned = _storage.ListObjects();
        await reached;
        await LetHangingCallTimeOutAsync();
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => abandoned);

        // Act
        var refused = _storage.ObjectExists("Notes.md");
        var refusedWasCompleted = refused.IsCompleted;
        var exception = await Record.ExceptionAsync(() => refused);
        var callCountWhileBlocked = _client.CallCount;
        _client.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !_storage.IsUnresponsive);
        var exists = await _storage.ObjectExists("Notes.md");

        // Assert
        Assert.True(refusedWasCompleted);
        Assert.IsType<StorageUnresponsiveException>(exception);
        Assert.Equal(1, callCountWhileBlocked);
        Assert.False(exists);
        Assert.Equal(2, _client.CallCount);
    }

    [Fact]
    public async Task WhenAbandonedOpenReturnsLate_ThenItsStreamIsDisposed()
    {
        // Arrange
        using var content = new MemoryStream([1, 2, 3]);
        await _storage.SetObject("Notes.md", content, append: false);
        var reached = _client.PauseNext(nameof(IStore.OpenRead));
        var open = _storage.OpenRead("Notes.md");
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
        var reached = _client.PauseNext(nameof(IStore.ListObjects));
        var call = _storage.ListObjects();
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
    public async Task WhenCallIsAbandonedByCancellation_ThenCallsAreRefusedUntilItReturnsAndItsResultIsDisposed()
    {
        // Arrange
        using var content = new MemoryStream([1, 2, 3]);
        await _storage.SetObject("Notes.md", content, append: false);
        using var cancellation = new CancellationTokenSource();
        var reached = _client.PauseNext(nameof(IStore.OpenRead));
        var open = _storage.OpenRead("Notes.md", cancellation.Token);
        await reached;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);

        // Act
        var refusal = await Record.ExceptionAsync(() => _storage.ObjectExists("Notes.md"));
        _client.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !_storage.IsUnresponsive);
        var exists = await _storage.ObjectExists("Notes.md");

        // Assert
        Assert.IsType<StorageUnresponsiveException>(refusal);
        Assert.True(exists);
        Assert.Equal(1, _client.OpenedStreamCount);
        Assert.Equal(1, _client.DisposedStreamCount);
    }

    [Fact]
    public async Task WhenOneOfTwoAbandonedCallsReturns_ThenCallsAreRefusedUntilTheOtherHasReturnedToo()
    {
        // Arrange
        var client = new TwoCallStore();
        using var storage = new TimeLimitedStore(client, _timeProvider, logger: null);
        var listing = storage.ListObjects();
        var exists = storage.ObjectExists("Notes.md");
        await client.WhenBothCallsHaveArrivedAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 2);
        _timeProvider.Advance(StorageCallTimeout.Limit);
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => listing);
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => exists);

        // Act: released on a thread of the pool, which then also runs what follows from the return of the call.
        await Task.Run(client.ReleaseListing);
        var isUnresponsiveAfterFirstReturn = storage.IsUnresponsive;
        await Task.Run(client.ReleaseExists);

        // Assert
        Assert.True(isUnresponsiveAfterFirstReturn);
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsUnresponsive);
    }

    [Fact]
    public async Task WhenLastOfTwoAbandonedCallsReturns_ThenStorageTellsOnceThatItAnswersAgain()
    {
        // Arrange
        var client = new TwoCallStore();
        var answersAgainCount = 0;
        using var storage = new TimeLimitedStore(
            client, _timeProvider, logger: null, whenAnswersAgain: () => Interlocked.Increment(ref answersAgainCount));
        var listing = storage.ListObjects();
        var exists = storage.ObjectExists("Notes.md");
        await client.WhenBothCallsHaveArrivedAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 2);
        _timeProvider.Advance(StorageCallTimeout.Limit);
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => listing);
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => exists);

        // Act: released on a thread of the pool, which then also runs what follows from the return of the call.
        await Task.Run(client.ReleaseListing);
        var countAfterFirstReturn = Volatile.Read(ref answersAgainCount);
        await Task.Run(client.ReleaseExists);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref answersAgainCount) > 0);
        Assert.Equal(0, countAfterFirstReturn);
        Assert.Equal(1, Volatile.Read(ref answersAgainCount));
        Assert.False(storage.IsUnresponsive);
    }

    [Fact]
    public async Task WhenCallExceedsTheLimit_ThenExceptionNamesOperationAndPath()
    {
        // Arrange
        var reached = _client.PauseNext(nameof(IStore.OpenRead));
        var open = _storage.OpenRead("Docs/Notes.md");
        await reached;

        // Act
        await LetHangingCallTimeOutAsync();
        var exception = await Assert.ThrowsAsync<StorageUnresponsiveException>(() => open);
        var refusal = await Assert.ThrowsAsync<StorageUnresponsiveException>(() => _storage.ObjectExists("Other.md"));

        // Assert
        Assert.Contains(nameof(IStore.OpenRead), exception.Message);
        Assert.Contains("Docs/Notes.md", exception.Message);
        Assert.Contains(nameof(IStore.ObjectExists), refusal.Message);
        Assert.Contains("Other.md", refusal.Message);
    }

    [Fact]
    public async Task WhenCallerCancels_ThenCallIsCanceledWithTheCallersToken()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var reached = _client.PauseNext(nameof(IStore.ListObjects));
        var call = _storage.ListObjects(cancellationToken: cancellation.Token);
        await reached;

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => call);

        // Assert
        var canceled = Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    public void Dispose() => _storage.Dispose();

    private async Task LetHangingCallTimeOutAsync()
    {
        // The call can arrive in the storage before its caller has started the wait that the limit ends.
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 1);
        _timeProvider.Advance(StorageCallTimeout.Limit);
    }

    /// <summary>
    /// A storage whose listing and whose check for existence each hang until the test lets them return.
    /// </summary>
    private sealed class TwoCallStore : StoreBase
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _listingArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _existsArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Without asynchronous continuations: the thread that completes one of them runs the rest of the call.
        private readonly TaskCompletionSource _listingReleased = new();
        private readonly TaskCompletionSource _existsReleased = new();

        public Task WhenBothCallsHaveArrivedAsync()
            => Task.WhenAll(_listingArrived.Task, _existsArrived.Task).WaitAsync(Timeout);

        public void ReleaseListing() => _listingReleased.SetResult();

        public void ReleaseExists() => _existsReleased.SetResult();

        public override async Task<List<StoreObject>> ListObjects(StorageListOptions? options = null, CancellationToken cancellationToken = default)
        {
            _listingArrived.SetResult();
            await _listingReleased.Task;
            return [];
        }

        public override async Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
        {
            _existsArrived.TrySetResult();
            await _existsReleased.Task;
            return false;
        }
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
