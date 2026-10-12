using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageTimeoutTests : StorageTestBase
{
    // A pass that waits for a call that never returns never completes, so the wait for it ends the test instead.
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

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
        File.Delete(GetFullPath("Second.md"));
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(["First.gated"], storage.Children.Keys);
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenNamedPassFails_ThenNextPassStillComparesTheNamedFileByContent()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectPausableAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        ChangeContentOnly("Data.gated", "other");
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
    public async Task WhenNamedPassFails_ThenNextNamedPassComparesTheNamedFilesOfBoth()
    {
        // Arrange
        WriteFile("First.gated", "first");
        WriteFile("Second.gated", "first");
        var storage = await ConnectPausableAsync();
        var first = (GatedFile)storage.Children["First.gated"];
        var second = (GatedFile)storage.Children["Second.gated"];
        ChangeContentOnly("First.gated", "other");
        ChangeContentOnly("Second.gated", "other");
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync(Named("First.gated"));
        await listingReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync(Named("Second.gated"));

        // Assert
        Assert.Equal("other", first.Content);
        Assert.Equal("other", second.Content);
    }

    [Fact]
    public async Task WhenPassWithEveryFileNamedFails_ThenNextPassComparesEveryFileByContent()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectPausableAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        ChangeContentOnly("Data.gated", "other");
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync(allNamed: true);
        await listingReached;

        // Act
        await LetHangingCallTimeOutAsync();
        await pass;
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("other", file.Content);
    }

    [Theory]
    [InlineData(StorageReconciler.MaximumUnfinishedNamedPathCount / 2, "first")]
    [InlineData(StorageReconciler.MaximumUnfinishedNamedPathCount / 2 + 1, "other")]
    public async Task WhenFailedPassesNamePaths_ThenNextPassComparesEveryFileOnlyWhenMoreWereNamedThanAreKept(
        int pathCountPerPass, string expectedContent)
    {
        // Arrange: the file changes without a new size or time, and no pass names it.
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        ChangeContentOnly("Data.gated", "other");
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                await storage.ReconcileAsync(Enumerable.Range(0, pathCountPerPass).Select(index => $"Camera/{pass}-{index}.jpg").ToHashSet());
            }
        }
        finally
        {
            Directory.Move(movedDirectory, StorageDirectory.FullName);
        }

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(expectedContent, file.Content);
    }

    [Fact]
    public async Task WhenPassFailsAtItsListing_ThenNextPassOpensNoUnchangedFile()
    {
        // Arrange
        WriteFile("First.md");
        WriteFile("Second.md");
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectPausableAsync();
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync();
        await listingReached;
        _client.FailHangingCall(new IOException("The share did not answer."));
        await pass;
        var statusAfterFailedPass = storage.Status;
        var openCountBeforeNextPass = _client.GetCallCount(nameof(IBlobStorage.OpenReadAsync));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(openCountBeforeNextPass, _client.GetCallCount(nameof(IBlobStorage.OpenReadAsync)));
    }

    [Fact]
    public async Task WhenNestedStorageStallsWhileItsChangedFileIsApplied_ThenPassOfParentCompletesAndDoesNotApplyTheFileAgain()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        WriteFile("Alpha.md");
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName, containerName: "first"));
        var parent = await ConnectAsync();
        var nested = Assert.IsType<FluentStorageContainer>(parent.Children["Nested"]);
        var nestedConnectCount = 0;
        Task? nestedListingReached = null;
        nested.TimeProvider = _timeProvider;
        nested.ClientDecorator = client =>
        {
            nestedConnectCount++;
            var nestedClient = new PausableBlobStorage(client);
            nestedListingReached = nestedClient.PauseNext(nameof(IBlobStorage.ListAsync));
            return nestedClient;
        };

        try
        {
            WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName, containerName: "second"));
            File.Delete(GetFullPath("Alpha.md"));
            WriteFile("Zulu.md");

            // Act: the pass of the parent connects the nested storage again, whose listing never returns.
            var pass = parent.ReconcileAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => nestedConnectCount == 1 && _timeProvider.ArmedTimerCount == 1);
            await nestedListingReached!;
            _timeProvider.Advance(StorageCallTimeout.Limit);
            await pass.WaitAsync(DeadlockTimeout);
            var statusAfterPass = parent.Status;
            var childrenAfterPass = parent.Children.Keys.Order().ToList();
            await parent.ReconcileAsync().WaitAsync(DeadlockTimeout);

            // Assert
            Assert.Equal(StorageStatus.Error, nested.Status);
            Assert.False(parent.IsStorageUnresponsive);
            Assert.Equal(StorageStatus.Connected, statusAfterPass);
            Assert.Equal(["Nested", "Zulu.md"], childrenAfterPass);
            Assert.Equal(1, nestedConnectCount);
        }
        finally
        {
            nested.Dispose();
        }
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
    public async Task WhenPassFailsAfterMovingSubject_ThenNextPassKeepsTheInstanceAttached()
    {
        // Arrange
        WriteFile("Old/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectPausableAsync();
        var motor = (Samples.Motor)((VirtualFolder)storage.Children["Old"]).Children["Motor"];
        var detaches = CountDetachesOf(motor);
        Directory.CreateDirectory(GetFullPath("Other"));
        File.Move(GetFullPath("Old/Motor.json"), GetFullPath("Other/Motor.json"));
        WriteFile("Zeta.gated");
        WriteFile("Zulu.md");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        gate.Release();
        await readReached;

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
    public async Task WhenVersionCannotBeReadAfterSubjectWasWritten_ThenSubjectIsAddedAndKeptByNextPass()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        var motor = CreateMotor("Pump");
        var versionReached = _client!.PauseNext(nameof(IBlobStorage.GetBlobsAsync));
        var add = storage.AddSubjectAsync("Motor.json", motor, CancellationToken.None);
        await versionReached;

        // Act
        await LetHangingCallTimeOutAsync();
        var exception = await Record.ExceptionAsync(() => add);
        var childrenAfterAdd = storage.Children.ToDictionary();
        await ReleaseHangingCallAsync(storage);
        await storage.ReconcileAsync();

        // Assert
        Assert.Null(exception);
        Assert.True(File.Exists(GetFullPath("Motor.json")));
        Assert.Same(motor, Assert.Single(childrenAfterAdd).Value);
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal(StorageStatus.Connected, storage.Status);
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

    /// <summary>
    /// Replaces the content of a file with content of the same length and keeps its modification time, so that
    /// a listing shows no change.
    /// </summary>
    private void ChangeContentOnly(string relativePath, string content)
    {
        var modified = File.GetLastWriteTimeUtc(GetFullPath(relativePath));
        WriteFile(relativePath, content);
        File.SetLastWriteTimeUtc(GetFullPath(relativePath), modified);
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
