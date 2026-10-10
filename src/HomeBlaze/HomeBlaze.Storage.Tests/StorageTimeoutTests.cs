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
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        gate.Release();
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
        Assert.Equal(2, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenPassFailsAndNextPassFindsNothingNew_ThenFilesLoadedByTheFailedPassArePlaced()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.gated");
        WriteFile("Second.md");
        var metadataReached = _client!.PauseNext(nameof(IBlobStorage.GetBlobsAsync));
        var pass = storage.ReconcileAsync();
        await metadataReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        File.Delete(GetFullPath("Second.md"));
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(["First.gated"], storage.Children.Keys);
        Assert.Equal(1, GatedFile.LoadCount);
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

        // Assert: the only stream before the metadata call is the one that hashed the file, not one of the subject.
        Assert.Equal(1, streamsOpenedBeforeMetadata);
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
        WriteFile("Zeta.md");
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
    public async Task WhenReadingMarkdownFileHangs_ThenOnlyThatFileIsLeftOutUntilAnEventNamesIt()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.md");
        WriteFile("Second.md");

        // The first stream of a file is hashed, the second one is read by its subject.
        var readReached = _client!.PauseReadOfStream(2);
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterPass = storage.Status;
        var childrenAfterPass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();
        var childrenAfterIdlePass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync(Named("First.md"));

        // Assert
        Assert.Equal(StorageStatus.Connected, statusAfterPass);
        Assert.Equal(["Second.md"], childrenAfterPass);
        Assert.Equal(["Second.md"], childrenAfterIdlePass);
        Assert.Equal(["First.md", "Second.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenReadingContentOfOneFileHangs_ThenOnlyThatFileIsLeftOutUntilItChanges()
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
        var statusAfterPass = storage.Status;
        var childrenAfterPass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();
        var childrenAfterIdlePass = storage.Children.Keys.ToList();
        WriteFile("First.json", SerializeMotor("First, changed"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Connected, statusAfterPass);
        Assert.Equal(["Second"], childrenAfterPass);
        Assert.Equal(["Second"], childrenAfterIdlePass);
        Assert.Equal(["First", "Second"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenHashingContentOfOneFileHangs_ThenOnlyThatFileIsLeftOutUntilAnEventNamesIt()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.gated");
        WriteFile("Second.gated");
        var readReached = _client!.PauseNext(PausableBlobStorage.ReadOperation);
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterPass = storage.Status;
        var childrenAfterPass = storage.Children.Keys.ToList();
        var loadCountAfterPass = GatedFile.LoadCount;
        await storage.ReconcileAsync(Named("First.gated"));

        // Assert
        Assert.Equal(StorageStatus.Connected, statusAfterPass);
        Assert.Equal(["Second.gated"], childrenAfterPass);
        Assert.Equal(1, loadCountAfterPass);
        Assert.Equal(["First.gated", "Second.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenHashingChangedFileHangs_ThenItKeepsItsContentUntilAnEventNamesIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectPausableAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        WriteFile("Data.gated", "second version");
        WriteFile("Other.md");
        var readReached = _client!.PauseNext(PausableBlobStorage.ReadOperation);
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        var statusAfterPass = storage.Status;
        var contentAfterPass = file.Content;
        await storage.ReconcileAsync();
        var contentAfterIdlePass = file.Content;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal(StorageStatus.Connected, statusAfterPass);
        Assert.Equal("first", contentAfterPass);
        Assert.Equal("first", contentAfterIdlePass);
        Assert.Equal(["Data.gated", "Other.md"], storage.Children.Keys.Order());
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenBlobWriteHitsTheLimitAndCallerDisposesItsStream_ThenFileStillGetsTheWholeContent()
    {
        // Arrange
        WriteFile("Notes.md", "first");
        var storage = await ConnectPausableAsync();
        var content = new MemoryStream("second version"u8.ToArray());
        var writeReached = _client!.PauseNext(nameof(IBlobStorage.WriteAsync));
        var write = storage.WriteBlobAsync("Notes.md", content, CancellationToken.None);
        await writeReached;

        // Act
        await LetHangingCallTimeOutAsync();
        var exception = await Record.ExceptionAsync(() => write);
        await content.DisposeAsync();
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<StorageUnresponsiveException>(exception);
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Notes.md")));
        Assert.Equal("second version", Assert.IsType<Files.MarkdownFile>(storage.Children["Notes.md"]).Content);
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

    [Fact]
    public async Task WhenFileEventsArriveWhilePassStallsOnTheStorage_ThenLaterPassLoadsEverything()
    {
        // Arrange: without a periodic pass, so that only file events start one.
        var storage = await ConnectPausableAsync(enableFileWatching: true, reconcileIntervalSeconds: 0);
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        WriteFile("First.md");
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == 1, WatcherTimeout);
        _timeProvider.Advance(ReconcileTrigger.MaximumDelay);
        await listingReached;
        var expectedChildren = Enumerable.Range(0, 10).Select(index => $"Later{index}.md").Append("First.md").Order().ToList();

        // Act
        foreach (var path in expectedChildren)
        {
            WriteFile(path);
        }

        await LetHangingCallTimeOutAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Error);
        var childrenAfterStalledPass = storage.Children.Keys.ToList();
        await ReleaseHangingCallAsync(storage);

        // Assert: every step of the clock runs the pass that the events of the stalled time asked for, if there is one.
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                _timeProvider.Advance(ReconcileTrigger.MaximumDelay);
                return storage.Status == StorageStatus.Connected && storage.Children.Count == expectedChildren.Count;
            },
            WatcherTimeout);

        Assert.Empty(childrenAfterStalledPass);
        Assert.Equal(expectedChildren, storage.Children.Keys.Order());
    }

    private async Task<FluentStorageContainer> ConnectPausableAsync(
        bool enableFileWatching = false, int reconcileIntervalSeconds = 300)
    {
        var storage = await ConnectAsync(enableFileWatching, container =>
        {
            container.TimeProvider = _timeProvider;
            container.ReconcileIntervalSeconds = reconcileIntervalSeconds;
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
