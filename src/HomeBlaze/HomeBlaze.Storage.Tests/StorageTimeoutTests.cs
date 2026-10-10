using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageTimeoutTests : StorageTestBase
{
    private readonly ManualTimeProvider _timeProvider = new();
    private PausableBlobStorage? _client;
    private int _idleTimerCount;

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
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Added.md", "Home.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenReadOfOneFileHangs_ThenPassFailsAndLaterPassLoadsTheFile()
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
        var statusAfterFailedPass = storage.Status;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["First.md", "Second.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenPassFailsAfterLoadingSomeFiles_ThenNextPassPlacesThemWithoutLoadingThemAgain()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.gated");
        WriteFile("Second.gated");
        var firstGate = GatedFile.PauseNextLoad();
        var secondGate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await firstGate.WhenReachedAsync();
        firstGate.Release();
        await secondGate.WhenReachedAsync();
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        secondGate.Release();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(["First.gated", "Second.gated"], storage.Children.Keys.Order());
        Assert.Equal(3, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenNamedPassFails_ThenNextPassComparesEveryFileByContent()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectPausableAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync(Named("Data.gated"));
        await listingReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenCallBlocksInsideTheClient_ThenCallerGetsUnresponsiveAndWorkerIsFree()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        var existsReached = _client!.BlockNext(nameof(IBlobStorage.ExistsAsync));
        var add = storage.AddSubjectAsync("Motor.json", CreateMotor(), CancellationToken.None);
        await existsReached;

        // Act
        await LetHangingCallTimeOutAsync();

        // Assert
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => add);
        await storage.ReconcileAsync();
        Assert.Equal(StorageStatus.Error, storage.Status);
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenMetadataCallBlocksWhileMarkdownFileLoads_ThenPassFailsAndLaterPassLoadsTheFile()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("Notes.md");
        var metadataReached = _client!.BlockNext(nameof(IBlobStorage.GetBlobsAsync));
        var pass = storage.ReconcileAsync();
        await metadataReached;
        var streamsOpenedBeforeMetadata = _client.OpenedStreamCount;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterFailedPass = storage.Status;
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(0, streamsOpenedBeforeMetadata);
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenPassFailsAfterMovingSubject_ThenNextPassKeepsTheInstanceAttached()
    {
        // Arrange
        WriteFile("Old/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectPausableAsync();
        var motor = (Samples.Motor)((VirtualFolder)storage.Children["Old"]).Children["Motor"];
        var detaches = CountDetachesOf(motor);
        Directory.CreateDirectory(GetFullPath("Other"));
        File.Move(GetFullPath("Old/Motor.json"), GetFullPath("Other/Motor.json"));
        WriteFile("Zeta.bin");
        var metadataReached = _client!.PauseNext(nameof(IBlobStorage.GetBlobsAsync));
        var pass = storage.ReconcileAsync();
        await metadataReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterFailedPass = storage.Status;
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        var other = Assert.IsType<VirtualFolder>(storage.Children["Other"]);
        Assert.Same(motor, other.Children["Motor"]);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenReadingContentOfOneFileHangs_ThenPassFailsAndLaterPassLoadsTheFile()
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
        var statusAfterFailedPass = storage.Status;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["First", "Second"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenHashingContentOfOneFileHangs_ThenPassFailsAndLaterPassLoadsTheFile()
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
        var statusAfterFailedPass = storage.Status;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["First.gated", "Second.gated"], storage.Children.Keys.Order());
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
        var exception = await Record.ExceptionAsync(() => add);
        var childrenAfterTimeout = storage.Children.Keys.ToList();
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<StorageUnresponsiveException>(exception);
        Assert.Empty(childrenAfterTimeout);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
    }

    private async Task<FluentStorageContainer> ConnectPausableAsync()
    {
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = _timeProvider;
            container.ClientDecorator = client => _client = new PausableBlobStorage(client);
        });

        _idleTimerCount = _timeProvider.ArmedTimerCount;
        return storage;
    }

    private async Task LetHangingCallTimeOutAsync()
    {
        // The call can arrive in the storage before its caller has started the wait that the limit ends.
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == _idleTimerCount + 1);
        _timeProvider.Advance(StorageCallTimeout.Limit);
    }

    private async Task ReleaseHangingCallAsync(FluentStorageContainer storage)
    {
        _client!.Release();

        // The call that was given up returns on another thread, and calls are refused until it has.
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);
    }
}
