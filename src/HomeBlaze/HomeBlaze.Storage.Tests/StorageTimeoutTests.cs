using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageTimeoutTests : StorageTestBase
{
    private readonly ManualTimeProvider _timeProvider = new();
    private PausableBlobStorage? _client;

    [Fact]
    public async Task WhenListingHangs_ThenPassFailsAndNextPassSucceeds()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectPausableAsync();
        WriteFile("Added.md");
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync();
        await listingReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterFailedPass = storage.Status;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Added.md", "Home.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenReadOfOneFileHangs_ThenOnlyThatFileIsMissing()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.md");
        WriteFile("Second.md");
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;

        // Assert
        Assert.Equal(["Second.md"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenReadingContentOfOneFileHangs_ThenOnlyThatFileIsMissing()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.json", SerializeMotor("First"));
        WriteFile("Second.json", SerializeMotor("Second"));
        var readReached = _client!.PauseNext(PausableBlobStorage.ReadOperation);
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;

        // Assert
        Assert.Equal(["Second"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenHashingContentOfOneFileHangs_ThenOnlyThatFileIsMissing()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.gated");
        WriteFile("Second.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        var readReached = _client!.PauseNext(PausableBlobStorage.ReadOperation);
        gate.Release();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;

        // Assert
        Assert.Equal(["Second.gated"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenWriteHangs_ThenCallerGetsTimeoutAndLaterWorkRuns()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        var writeReached = _client!.PauseNext(nameof(IBlobStorage.WriteAsync));
        var add = storage.AddSubjectAsync("Motor.json", CreateMotor(), CancellationToken.None);
        await writeReached;

        // Act
        await LetHangingCallTimeOutAsync();

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => add);
        await storage.ReconcileAsync();
        Assert.Empty(storage.Children);
    }

    private async Task<FluentStorageContainer> ConnectPausableAsync()
    {
        var storage = await ConnectAsync(configure: storage =>
        {
            storage.TimeProvider = _timeProvider;
            storage.ClientDecorator = client => _client = new PausableBlobStorage(client);
        });

        // The wait of a call that has completed holds its timer a moment longer. Gone now, it cannot be taken
        // for the wait of the call that hangs.
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 0);
        return storage;
    }

    private async Task LetHangingCallTimeOutAsync()
    {
        // A call has arrived in the storage before its caller starts the wait that the limit ends.
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 1);
        _timeProvider.Advance(StorageCallTimeout.Limit);
    }
}
