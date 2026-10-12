using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerReconcileTests : StorageTestBase
{
    [Fact]
    public async Task WhenFileAppears_ThenPassAddsIt()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Notes.md");

        // Act
        await storage.ReconcileAsync(Named("Notes.md"));

        // Assert
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenFileIsGone_ThenPassRemovesItsSubject()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        File.Delete(GetFullPath("Home.md"));

        // Act
        await storage.ReconcileAsync(Named("Home.md"));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenFileIsRenamed_ThenSubjectMovesToNewName()
    {
        // Arrange
        WriteFile("Old.md");
        var storage = await ConnectAsync();
        File.Move(GetFullPath("Old.md"), GetFullPath("New.md"));

        // Act
        await storage.ReconcileAsync(Named("Old.md", "New.md"));

        // Assert
        Assert.Equal(["New.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenOnlyCasingOfFileNameChanges_ThenSubjectMovesToNewCasing()
    {
        // Arrange
        WriteFile("notes.md");
        var storage = await ConnectAsync();
        File.Move(GetFullPath("notes.md"), GetFullPath("Notes.md"));

        // Act
        await storage.ReconcileAsync(Named("notes.md", "Notes.md"));

        // Assert
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal("Notes.md", Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]).Name);
    }

    [Fact]
    public async Task WhenOnlyCasingOfDirectoryNameChanges_ThenFolderMovesToNewCasing()
    {
        // Arrange
        WriteFile("docs/Readme.md");
        var storage = await ConnectAsync();
        Directory.Move(GetFullPath("docs"), GetFullPath("docs-renaming"));
        Directory.Move(GetFullPath("docs-renaming"), GetFullPath("Docs"));

        // Act
        await storage.ReconcileAsync(Named("docs", "Docs"));

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Theory]
    [InlineData("Docs/.hidden.md")]
    [InlineData("Docs/Old~/Notes.md")]
    public async Task WhenIgnoredFileIsCreatedInVisibleDirectory_ThenOnlyTheDirectoryIsAdded(string relativePath)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(relativePath);

        // Act
        await storage.ReconcileAsync(Named(relativePath));

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.Empty(Assert.IsType<VirtualFolder>(storage.Children["Docs"]).Children);
    }

    [Fact]
    public async Task WhenDirectoryIsCreated_ThenVirtualFolderIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();
        Directory.CreateDirectory(GetFullPath("Docs"));

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public async Task WhenDirectoryWithFilesIsCreated_ThenContainedFilesAreAdded()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Docs/Readme.md");
        WriteFile("Docs/Guides/Setup.md");
        WriteFile("Docs/.hidden.md");

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Guides", "Readme.md"], docs.Children.Keys.Order());
        var guides = Assert.IsType<VirtualFolder>(docs.Children["Guides"]);
        Assert.Equal(["Setup.md"], guides.Children.Keys);
    }

    [Fact]
    public async Task WhenDirectoryIsDeleted_ThenFolderAndContainedFilesAreRemoved()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        WriteFile("Docs/Guides/Setup.md");
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        Directory.Delete(GetFullPath("Docs"), recursive: true);

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        Assert.Equal(["Home.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenDirectoryIsRenamed_ThenFolderMovesWithContainedFiles()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        Directory.Move(GetFullPath("Docs"), GetFullPath("Archive"));

        // Act
        await storage.ReconcileAsync(Named("Docs", "Archive"));

        // Assert
        Assert.Equal(["Archive"], storage.Children.Keys);
        var archive = Assert.IsType<VirtualFolder>(storage.Children["Archive"]);
        Assert.Equal(["Readme.md"], archive.Children.Keys);
    }

    [Fact]
    public async Task WhenDirectoryIsReplacedByAnotherOfSameName_ThenFolderMatchesNewDirectory()
    {
        // Arrange
        WriteFile("Docs/OnlyOld.md", "old");
        WriteFile("Docs/Both.md", "old");
        WriteFile("Docs/OldGuides/Setup.md", "old");
        var storage = await ConnectAsync();
        var both = Assert.IsType<MarkdownFile>(((VirtualFolder)storage.Children["Docs"]).Children["Both.md"]);

        Directory.Delete(GetFullPath("Docs"), recursive: true);
        WriteFile("Docs/Both.md", "new");
        WriteFile("Docs/OnlyNew.md", "new");

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Both.md", "OnlyNew.md"], docs.Children.Keys.Order());
        Assert.Same(both, docs.Children["Both.md"]);
        Assert.Equal("new", both.Content);
    }

    [Fact]
    public async Task WhenFileReplacesDirectoryOfSameName_ThenFileIsAdded()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        WriteFile("Docs");

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.IsNotType<VirtualFolder>(storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenDirectoryReplacesFileOfSameName_ThenFolderWithItsFilesIsAdded()
    {
        // Arrange
        WriteFile("Docs");
        var storage = await ConnectAsync();
        var childBefore = storage.Children["Docs"];
        File.Delete(GetFullPath("Docs"));
        WriteFile("Docs/Readme.md");

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        Assert.IsType<GenericFile>(childBefore);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenAddedFileClashesWithFolderKey_ThenDeletingItKeepsTheFolder()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();

        WriteFile("Docs.json", SerializeMotor("Motor"));
        await storage.ReconcileAsync(Named("Docs.json"));
        Assert.Equal(["Docs"], storage.Children.Keys);
        File.Delete(GetFullPath("Docs.json"));

        // Act
        await storage.ReconcileAsync(Named("Docs.json"));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageIsResynchronized_ThenTreeMatchesDiskAndExistingSubjectsAreKept()
    {
        // Arrange
        WriteFile("Kept.md", "first");
        WriteFile("Docs/Deleted.md");
        WriteFile("Gone/Old.md");
        var storage = await ConnectAsync();
        var kept = Assert.IsType<MarkdownFile>(storage.Children["Kept.md"]);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        WriteFile("Kept.md", "second");
        File.Delete(GetFullPath("Docs/Deleted.md"));
        Directory.Delete(GetFullPath("Gone"), recursive: true);
        WriteFile("Docs/Added.md");
        WriteFile("New/Added.md");

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Equal(["Docs", "Kept.md", "New"], storage.Children.Keys.Order());
        Assert.Same(kept, storage.Children["Kept.md"]);
        Assert.Equal("second", kept.Content);
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Equal(["Added.md"], docs.Children.Keys);
        Assert.Equal(["Added.md"], Assert.IsType<VirtualFolder>(storage.Children["New"]).Children.Keys);
    }

    [Fact]
    public async Task WhenStorageIsResynchronized_ThenUnchangedConfigurableSubjectIsNotReloaded()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        motor.Name = "Changed in memory";

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("Changed in memory", motor.Name);
    }

    [Fact]
    public async Task WhenFileIsDeletedWhileItsSubjectLoads_ThenNextPassRemovesIt()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated");

        var gate = GatedFile.PauseNextLoad();
        var add = storage.ReconcileAsync(Named("Data.gated"));
        await gate.WhenReachedAsync();

        File.Delete(GetFullPath("Data.gated"));
        var delete = storage.ReconcileAsync(Named("Data.gated"));

        // Act
        gate.Release();
        await Task.WhenAll(add, delete);

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenFileIsAddedWhilePassRuns_ThenFileIsKept()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");

        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        WriteFile("Added.md");
        var added = storage.ReconcileAsync(Named("Added.md"));

        // Act
        gate.Release();
        await Task.WhenAll(pass, added);

        // Assert
        Assert.Equal(["Added.md", "Slow.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenSubjectIsAddedWithTakenName_ThenItThrowsAndExistingSubjectIsKept()
    {
        // Arrange
        var originalJson = SerializeMotor("Existing");
        WriteFile("Motor.json", originalJson);
        var storage = await ConnectAsync();
        var existing = storage.Children["Motor"];
        var added = CreateMotor("Added");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.AddSubjectAsync("Motor.json", added, CancellationToken.None));

        Assert.Same(existing, storage.Children["Motor"]);
        Assert.Equal(originalJson, File.ReadAllText(GetFullPath("Motor.json")));
    }

    [Fact]
    public async Task WhenSubjectIsAddedWithKeyOfFolder_ThenItThrowsAndNoFileIsWritten()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var added = CreateMotor();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.AddSubjectAsync("Docs.json", added, CancellationToken.None));

        Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.False(File.Exists(GetFullPath("Docs.json")));
    }

    [Fact]
    public async Task WhenSubjectIsAddedBelowTakenKey_ThenItThrowsAndNoFolderIsCreated()
    {
        // Arrange
        WriteFile("Devices.json", SerializeMotor("Existing"));
        var storage = await ConnectAsync();
        var added = CreateMotor();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.AddSubjectAsync("Devices/Motor.json", added, CancellationToken.None));

        Assert.Equal(["Devices"], storage.Children.Keys);
        Assert.False(Directory.Exists(GetFullPath("Devices")));
    }

    [Theory]
    [InlineData(".Motor.json")]
    [InlineData(".idea/Motor.json")]
    [InlineData("Motor.json.tmp")]
    public async Task WhenSubjectIsAddedWithIgnoredName_ThenItThrowsAndNoFileIsWritten(string path)
    {
        // Arrange
        var storage = await ConnectAsync();
        var added = CreateMotor();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.AddSubjectAsync(path, added, CancellationToken.None));

        Assert.Empty(storage.Children);
        Assert.False(File.Exists(GetFullPath(path)));
    }

    [Fact]
    public async Task WhenSubjectIsAdded_ThenItIsWrittenAndPlaced()
    {
        // Arrange
        var storage = await ConnectAsync();
        var added = CreateMotor("Added");

        // Act
        await storage.AddSubjectAsync("Devices/Motor.json", added, CancellationToken.None);

        // Assert
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.Same(added, devices.Children["Motor"]);
        Assert.Contains("Added", File.ReadAllText(GetFullPath("Devices/Motor.json")));
    }

    [Fact]
    public async Task WhenSubjectIsAdded_ThenNamedPassDoesNotReloadIt()
    {
        // Arrange
        var storage = await ConnectAsync();
        var added = CreateMotor("Added");
        await storage.AddSubjectAsync("Devices/Motor.json", added, CancellationToken.None);
        added.Name = "Only in memory";

        // Act
        await storage.ReconcileAsync(Named("Devices/Motor.json"));

        // Assert
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.Same(added, devices.Children["Motor"]);
        Assert.Equal("Only in memory", added.Name);
    }

    [Fact]
    public async Task WhenTemporaryDirectoryIsRenamedToRealName_ThenOnlyRealFolderExists()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Docs.tmp/Readme.md");
        await storage.ReconcileAsync(Named("Docs.tmp/Readme.md"));
        Directory.Move(GetFullPath("Docs.tmp"), GetFullPath("Docs"));

        // Act
        await storage.ReconcileAsync(Named("Docs.tmp", "Docs"));

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenFileWatchingIsDisabledByReconfiguration_ThenPreviousWatcherStops()
    {
        // Arrange
        var reconfigured = await ConnectAsync(enableFileWatching: true);
        reconfigured.EnableFileWatching = false;
        await reconfigured.ApplyConfigurationAsync(CancellationToken.None);

        var watching = await ConnectAsync(enableFileWatching: true);

        // Act: the second file is handled a full quiet period after the first,
        // so by then every watcher that is still running has handled the first one.
        WriteFile("First.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("First.md"), WatcherTimeout);
        WriteFile("Second.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("Second.md"), WatcherTimeout);

        // Assert
        Assert.Empty(reconfigured.Children);
    }

    [Fact]
    public async Task WhenUnchangedConfigurationIsApplied_ThenEverySubjectKeepsItsInstance()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Docs/Notes.md");
        var storage = await ConnectAsync(configure: container => container.TimeProvider = timeProvider);
        var motor = storage.Children["Motor"];
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var notes = docs.Children["Notes.md"];
        var detaches = CountDetachesOf(motor);
        WriteFile("Added.md");

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert: a reconnect would also have loaded the file that no pass has seen yet.
        Assert.Equal(["Docs", "Motor"], storage.Children.Keys.Order());
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Same(notes, docs.Children["Notes.md"]);
        Assert.Equal(0, detaches.Count);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(1, timeProvider.ArmedTimerCount);
    }

    [Theory]
    [InlineData(nameof(FluentStorageContainer.StorageType))]
    [InlineData(nameof(FluentStorageContainer.ConnectionString))]
    [InlineData(nameof(FluentStorageContainer.ContainerName))]
    [InlineData(nameof(FluentStorageContainer.EnableFileWatching))]
    [InlineData(nameof(FluentStorageContainer.ReconcileIntervalSeconds))]
    public async Task WhenOneConnectionSettingChanges_ThenApplyingConfigurationReconnects(string setting)
    {
        // Arrange
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        var notes = storage.Children["Notes.md"];
        WriteFile("Added.md");

        switch (setting)
        {
            case nameof(FluentStorageContainer.StorageType):
                storage.StorageType = "filesystem";
                break;
            case nameof(FluentStorageContainer.ConnectionString):
                storage.ConnectionString = StorageDirectory.FullName + Path.DirectorySeparatorChar;
                break;
            case nameof(FluentStorageContainer.ContainerName):
                storage.ContainerName = "other";
                break;
            case nameof(FluentStorageContainer.EnableFileWatching):
                storage.EnableFileWatching = true;
                break;
            default:
                storage.ReconcileIntervalSeconds = 60;
                break;
        }

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Added.md", "Notes.md"], storage.Children.Keys.Order());
        Assert.NotSame(notes, storage.Children["Notes.md"]);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenUnchangedConfigurationIsAppliedAfterConnectFailed_ThenStorageConnectsAgain()
    {
        // Arrange
        var missingDirectory = Path.Combine(StorageDirectory.FullName, "Missing");
        FluentStorageContainer? storage = null;
        var exception = await Record.ExceptionAsync(() => ConnectAsync(enableFileWatching: true, configure: container =>
        {
            storage = container;
            container.ConnectionString = missingDirectory;
        }));
        var statusAfterFailedConnect = storage!.Status;
        WriteFile("Missing/Notes.md");

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(exception);
        Assert.Equal(StorageStatus.Error, statusAfterFailedConnect);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenNoEventArrives_ThenPeriodicPassPicksUpTheChange()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        WriteFile("Added.md");

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Children.ContainsKey("Added.md"), WatcherTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WhenReconcileIntervalIsZeroOrNegative_ThenNoPeriodicPassIsScheduled(int reconcileIntervalSeconds)
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();

        // Act
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = reconcileIntervalSeconds;
        });

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
    }

    [Fact]
    public async Task WhenStorageDirectoryIsMissingAtConnect_ThenStorageLoadsAndIsWatchedOnceItExists()
    {
        // Arrange: without a periodic pass, so that only the retry of the failed pass and file events start one.
        var timeProvider = new ManualTimeProvider();
        FluentStorageContainer? storage = null;
        var exception = await Record.ExceptionAsync(() => ConnectAsync(enableFileWatching: true, configure: container =>
        {
            storage = container;
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 0;
            container.ConnectionString = Path.Combine(StorageDirectory.FullName, "Missing");
        }));
        var statusAfterConnect = storage!.Status;

        // Act: the retry finds the directory and starts the file watcher, whose events lead to the passes after it.
        WriteFile("Missing/Notes.md");
        timeProvider.Advance(ReconcileTrigger.MaximumDelay);
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Connected, WatcherTimeout);
        var childrenAfterRetry = storage.Children.Keys.ToList();
        WriteFile("Missing/Added.md");

        // Assert
        await WaitWhileClockRunsAsync(timeProvider, () => storage.Children.ContainsKey("Added.md"));
        Assert.IsType<IOException>(exception, exactMatch: false);
        Assert.Equal(StorageStatus.Error, statusAfterConnect);
        Assert.Equal(["Notes.md"], childrenAfterRetry);
    }

    [Fact]
    public async Task WhenFileWatcherCannotStartWhileStorageCanBeRead_ThenStorageIsLoadedAndWatchedFromLaterPassOn()
    {
        // Arrange: the directory appears right after the file watcher failed to start, which is when the
        // warning about it is logged. Without a periodic pass, so that only file events start a pass.
        var timeProvider = new ManualTimeProvider();
        var directory = Path.Combine(StorageDirectory.FullName, "Late");
        var logger = new CallbackLogger((level, _) =>
        {
            if (level == LogLevel.Warning && !Directory.Exists(directory))
            {
                WriteFile("Late/Notes.md");
            }
        });
        var storage = CreateStorage(enableFileWatching: true, logger: logger, configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 0;
            container.ConnectionString = directory;
        });

        // Act
        await storage.ConnectAsync(CancellationToken.None);
        var statusAfterConnect = storage.Status;
        var childrenAfterConnect = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();
        WriteFile("Late/Added.md");

        // Assert
        await WaitWhileClockRunsAsync(timeProvider, () => storage.Children.ContainsKey("Added.md"));
        Assert.Equal(StorageStatus.Connected, statusAfterConnect);
        Assert.Equal(["Notes.md"], childrenAfterConnect);
    }

    [Fact]
    public async Task WhenLaterPassStartsTheFileWatcher_ThenOnlyThatPassComparesEveryFileByContent()
    {
        // Arrange: the file watcher cannot start at connect, because the directory appears only when the
        // warning about that is logged. While nothing watches, the page changes without a new size or time.
        var directory = Path.Combine(StorageDirectory.FullName, "Late");
        var logger = new CallbackLogger((level, _) =>
        {
            if (level == LogLevel.Warning && !Directory.Exists(directory))
            {
                WriteFile("Late/Page.md", "aaaa");
            }
        });
        var storage = CreateStorage(enableFileWatching: true, logger: logger, configure: container =>
        {
            // A clock that does not move, so that only the test runs a pass.
            container.TimeProvider = new ManualTimeProvider();
            container.ReconcileIntervalSeconds = 0;
            container.ConnectionString = directory;
        });
        await storage.ConnectAsync(CancellationToken.None);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var pagePath = Path.Combine(directory, "Page.md");
        var modified = File.GetLastWriteTimeUtc(pagePath);
        File.WriteAllText(pagePath, "bbbb");
        File.SetLastWriteTimeUtc(pagePath, modified);

        // Act: the first pass starts the file watcher, the second one finds it running.
        await storage.ReconcileAsync();
        var contentAfterPassThatStartsWatcher = page.Content;
        File.WriteAllText(pagePath, "cccc");
        File.SetLastWriteTimeUtc(pagePath, modified);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("bbbb", contentAfterPassThatStartsWatcher);
        Assert.Equal("bbbb", page.Content);
    }

    [Fact]
    public async Task WhenFileWatcherCannotStartAgainAfterItRan_ThenThatIsLoggedAsWarningOncePerOutage()
    {
        // Arrange: the file watcher cannot start at connect, because the directory appears only when the
        // warning about that is logged. A later pass starts it.
        var directory = Path.Combine(StorageDirectory.FullName, "Late");
        var warningCount = 0;
        var logger = new CallbackLogger((level, message) =>
        {
            if (level == LogLevel.Warning && message.StartsWith("File watching cannot start", StringComparison.Ordinal) &&
                Interlocked.Increment(ref warningCount) == 1)
            {
                WriteFile("Late/Notes.md");
            }
        });
        var storage = CreateStorage(enableFileWatching: true, logger: logger, configure: container =>
        {
            // A clock that does not move, so that only the test runs a pass.
            container.TimeProvider = new ManualTimeProvider();
            container.ReconcileIntervalSeconds = 0;
            container.ConnectionString = directory;
        });
        await storage.ConnectAsync(CancellationToken.None);
        await storage.ReconcileAsync();
        var warningCountAfterFirstOutage = Volatile.Read(ref warningCount);

        // Act: the file watcher fails, and cannot start in the two passes that follow.
        Directory.Delete(directory, recursive: true);
        storage.SimulateFileWatcherError(new IOException("The watched directory is gone."));
        await storage.ReconcileAsync();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(1, warningCountAfterFirstOutage);
        Assert.Equal(2, Volatile.Read(ref warningCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenFileWatcherStallsWhileStorageConnects_ThenStorageCanStillBeStoppedOrDisposed(bool isDisposed)
    {
        // Arrange: the start of the file watcher is held where it logs that it watches, which is within the start.
        var timeProvider = new ManualTimeProvider();
        var watcherStartReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var logger = new CallbackLogger((_, message) =>
        {
            if (message.StartsWith(WatcherStartedMessage, StringComparison.Ordinal))
            {
                watcherStartReached.TrySetResult();
                release.Wait(WatcherTimeout);
            }
        });
        WriteFile("Notes.md");
        var storage = CreateStorage(enableFileWatching: true, logger: logger, configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        var connect = Task.Run(() => storage.ConnectAsync(CancellationToken.None));
        await watcherStartReached.Task.WaitAsync(WatcherTimeout);

        // Act
        var stopOrDispose = Task.Run(() =>
        {
            if (isDisposed)
            {
                storage.Dispose();
                return Task.CompletedTask;
            }

            return storage.StopAsync(CancellationToken.None);
        });
        Exception? exceptionWhileWatcherStarts;
        try
        {
            exceptionWhileWatcherStarts = await Record.ExceptionAsync(() => stopOrDispose.WaitAsync(WatcherTimeout));
        }
        finally
        {
            release.Set();
        }

        var connectException = await Record.ExceptionAsync(() => connect);

        // Assert: neither the trigger nor a pass of the connection is left running.
        Assert.Null(exceptionWhileWatcherStarts);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
        if (isDisposed)
        {
            Assert.IsType<OperationCanceledException>(connectException, exactMatch: false);
            Assert.Equal(StorageStatus.Disconnected, storage.Status);
            Assert.Empty(storage.Children);
        }
        else
        {
            Assert.Null(connectException);
            Assert.Equal(StorageStatus.Connected, storage.Status);
            Assert.Equal(["Notes.md"], storage.Children.Keys);
        }
    }

    [Fact]
    public async Task WhenFileWatcherStallsWhileLaterPassStartsIt_ThenAnotherPassStillChangesTheStatus()
    {
        // Arrange: the file watcher cannot start at connect, because the directory appears only when the
        // warning about that is logged. The start that a later pass tries is held where it logs that it watches.
        var timeProvider = new ManualTimeProvider();
        var directory = Path.Combine(StorageDirectory.FullName, "Late");
        var watcherStartReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        PausableBlobStorage? client = null;
        var logger = new CallbackLogger((level, message) =>
        {
            if (level == LogLevel.Warning && !Directory.Exists(directory))
            {
                WriteFile("Late/Notes.md");
            }
            else if (message.StartsWith(WatcherStartedMessage, StringComparison.Ordinal))
            {
                watcherStartReached.TrySetResult();
                release.Wait(WatcherTimeout);
            }
        });
        var storage = CreateStorage(enableFileWatching: true, logger: logger, configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 0;
            container.ConnectionString = directory;
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
        });
        await storage.ConnectAsync(CancellationToken.None);
        var statusAfterConnect = storage.Status;
        var passThatStartsWatcher = Task.Run(() => storage.ReconcileAsync());
        await watcherStartReached.Task.WaitAsync(WatcherTimeout);

        // Act
        StorageStatus statusWhileWatcherStarts;
        try
        {
            var listingReached = client!.PauseNext(nameof(IBlobStorage.ListAsync));
            var otherPass = storage.ReconcileAsync();
            await listingReached;
            client.FailHangingCall(new IOException("The share did not answer."));
            await otherPass.WaitAsync(WatcherTimeout);
            statusWhileWatcherStarts = storage.Status;
        }
        finally
        {
            release.Set();
        }

        await passThatStartsWatcher;

        // Assert
        Assert.Equal(StorageStatus.Connected, statusAfterConnect);
        Assert.Equal(StorageStatus.Error, statusWhileWatcherStarts);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenListingOfPeriodicPassFailsOnce_ThenPassIsRetriedWithoutAnyEvent()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        PausableBlobStorage? client = null;
        WriteFile("Home.md");
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 300;
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
        });
        WriteFile("Added.md");
        var listingReached = client!.PauseNext(nameof(IBlobStorage.ListAsync));
        timeProvider.Advance(TimeSpan.FromSeconds(300));
        await listingReached;
        client.FailHangingCall(new FileNotFoundException("A file vanished while the storage was listed."));

        // The periodic timer and the timer of the retry.
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Error && timeProvider.ArmedTimerCount == 2);

        // Act
        timeProvider.Advance(ReconcileTrigger.MaximumDelay);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Connected);
        Assert.Equal(["Added.md", "Home.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenFirstPassFails_ThenItIsRetriedWithoutAnyEvent()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        PausableBlobStorage? client = null;
        Task? listingReached = null;
        FluentStorageContainer? storage = null;
        WriteFile("Home.md");
        var connect = ConnectAsync(configure: container =>
        {
            storage = container;
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 0;
            container.ClientDecorator = inner =>
            {
                client = new PausableBlobStorage(inner);
                listingReached = client.PauseNext(nameof(IBlobStorage.ListAsync));
                return client;
            };
        });
        await listingReached!;
        client!.FailHangingCall(new IOException("The share did not answer."));
        var exception = await Record.ExceptionAsync(() => connect);
        var statusAfterConnect = storage!.Status;

        // Act
        timeProvider.Advance(ReconcileTrigger.MaximumDelay);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Connected);
        Assert.IsType<IOException>(exception);
        Assert.Equal(StorageStatus.Error, statusAfterConnect);
        Assert.Equal(["Home.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageAnswersAgainAfterRetryWasRefused_ThenPassRunsAtOnce()
    {
        // Arrange
        WriteFile("Home.md");
        var stalled = await StallPeriodicPassUntilItsRetryIsRefusedAsync();
        var callCountBeforeRetry = stalled.CallCountBeforeRetry;
        var callCountAfterRetry = stalled.Client.CallCount;
        var statusWhileStorageRefuses = stalled.Storage.Status;
        WriteFile("Added.md");

        // Act: the periodic timer and the timer of the pass that is asked for.
        stalled.Client.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => stalled.TimeProvider.ArmedTimerCount == 2);
        stalled.TimeProvider.Advance(TimeSpan.Zero);

        // Assert: the retry did not reach the storage, and nothing waits for the periodic pass.
        await AsyncTestHelpers.WaitUntilAsync(() => stalled.Storage.Status == StorageStatus.Connected);
        Assert.Equal(callCountBeforeRetry, callCountAfterRetry);
        Assert.Equal(StorageStatus.Error, statusWhileStorageRefuses);
        Assert.Equal(["Added.md", "Home.md"], stalled.Storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenRetryOfPassIsRefused_ThenEveryOtherRetryIsStillLeft()
    {
        // Arrange
        WriteFile("Home.md");
        var stalled = await StallPeriodicPassUntilItsRetryIsRefusedAsync();
        var timeProvider = stalled.TimeProvider;
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);

        int armedTimersAfterLastRetry;
        try
        {
            // Act: the storage answers again, but its directory is gone, so every pass from here on fails.
            // Each wait is for the periodic timer and the timer of the next pass.
            stalled.Client.Release();
            await AsyncTestHelpers.WaitUntilAsync(() => timeProvider.ArmedTimerCount == 2);
            timeProvider.Advance(TimeSpan.Zero);
            for (var failedPassCount = 3; failedPassCount < 2 + ReconcileTrigger.MaximumRetryCount; failedPassCount++)
            {
                var expectedCount = failedPassCount;
                await AsyncTestHelpers.WaitUntilAsync(() => stalled.GetFailedPassCount() == expectedCount && timeProvider.ArmedTimerCount == 2);
                timeProvider.Advance(ReconcileTrigger.MaximumDelay);
            }

            await AsyncTestHelpers.WaitUntilAsync(() => stalled.GetFailedPassCount() == 2 + ReconcileTrigger.MaximumRetryCount);
            await AsyncTestHelpers.WaitUntilAsync(() => timeProvider.ArmedTimerCount == 1);
            armedTimersAfterLastRetry = timeProvider.ArmedTimerCount;
        }
        finally
        {
            PutStorageDirectoryBack(movedDirectory);
        }

        // Assert: after the pass that stalled and its refused retry, the pass that was asked for is the first
        // of the retries that are left, and nothing follows the last of them but the periodic pass.
        Assert.Equal(1, armedTimersAfterLastRetry);
        Assert.Equal(StorageStatus.Error, stalled.Storage.Status);
    }

    [Fact]
    public async Task WhenFirstPassIsRefused_ThenNoRetryIsArmedAndPassRunsOnceStorageAnswersAgain()
    {
        // Arrange: a read arrives once the new connection can be found, which is before its first pass lists.
        // The caller of the read gives up, and the read has not returned when the first pass lists.
        var timeProvider = new ManualTimeProvider();
        var hookedTimeProvider = new HookedTimeProvider(timeProvider);
        PausableBlobStorage? client = null;
        Task? read = null;
        using var cancellation = new CancellationTokenSource();
        WriteFile("Home.md");
        var storage = CreateStorage(configure: container =>
        {
            container.TimeProvider = hookedTimeProvider;
            container.ReconcileIntervalSeconds = 0;
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
        });
        hookedTimeProvider.BeforeNextTimerIsCreated(() =>
        {
            var readReached = client!.PauseNext(nameof(IBlobStorage.GetBlobsAsync));
            read = storage.GetBlobMetadataAsync("Home.md", cancellation.Token);
            readReached.Wait(WatcherTimeout);
            cancellation.Cancel();
            SpinWait.SpinUntil(() => storage.IsStorageUnresponsive, WatcherTimeout);
        });

        // Act
        var exception = await Record.ExceptionAsync(() => storage.ConnectAsync(CancellationToken.None));
        var statusAfterConnect = storage.Status;
        var armedTimersAfterConnect = timeProvider.ArmedTimerCount;
        await Record.ExceptionAsync(() => read!);
        client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive && timeProvider.ArmedTimerCount == 1);
        timeProvider.Advance(TimeSpan.Zero);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Connected);
        Assert.IsType<StorageUnresponsiveException>(exception);
        Assert.Equal(StorageStatus.Error, statusAfterConnect);
        Assert.Equal(0, armedTimersAfterConnect);
        Assert.Equal(["Home.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageDirectoryIsGone_ThenPassFailsAndTreeIsKeptUntilItIsBack()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Docs/Page.md");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var docs = storage.Children["Docs"];
        var detaches = CountDetachesOf(motor);
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);

        StorageStatus statusWhileGone;
        List<string> childrenWhileGone;
        try
        {
            // Act
            await storage.ReconcileAsync();
            statusWhileGone = storage.Status;
            childrenWhileGone = storage.Children.Keys.Order().ToList();
        }
        finally
        {
            Directory.Move(movedDirectory, StorageDirectory.FullName);
        }

        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusWhileGone);
        Assert.Equal(["Docs", "Motor"], childrenWhileGone);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenSubjectIsSavedWhileStorageDirectoryIsGone_ThenNextPassStillFailsAndKeepsEverySubject()
    {
        // Arrange: the directory is gone, and the pass that sees it fails and keeps the tree.
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Docs/Page.md");
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var detaches = CountDetachesOf(storage.Children["Notes.md"]);
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);

        Exception? exception;
        bool isDirectoryCreated;
        StorageStatus statusAfterNextPass;
        List<string> childrenAfterNextPass;
        try
        {
            await storage.ReconcileAsync();

            // Act
            motor.Name = "Saved while the directory was gone";
            exception = await Record.ExceptionAsync(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));
            isDirectoryCreated = Directory.Exists(StorageDirectory.FullName);
            await storage.ReconcileAsync();
            statusAfterNextPass = storage.Status;
            childrenAfterNextPass = storage.Children.Keys.Order().ToList();
        }
        finally
        {
            PutStorageDirectoryBack(movedDirectory);
        }

        // Assert
        Assert.IsType<IOException>(exception, exactMatch: false);
        Assert.False(isDirectoryCreated);
        Assert.Equal(StorageStatus.Error, statusAfterNextPass);
        Assert.Equal(["Docs", "Motor", "Notes.md"], childrenAfterNextPass);
        Assert.Equal(0, detaches.Count);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("add into new folder")]
    [InlineData("write blob")]
    [InlineData("write blob in folder")]
    [InlineData("write new blob")]
    [InlineData("save configuration")]
    [InlineData("save embedded subject")]
    [InlineData("delete blob")]
    [InlineData("delete subject")]
    public async Task WhenStorageIsChangedWhileItsDirectoryIsGone_ThenOperationFailsAsThePassDoesAndDirectoryIsNotCreated(string operation)
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Docs/Page.md", "first");
        WriteFile("Notes.md", "first");
        WriteFile("Embedding.md", $"# Page\n\n```subject(motor)\n{SerializeMotor("Embedded")}\n```\n");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Embedding.md"]);
        var contentBefore = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]).Content;
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);

        Exception? exception;
        bool isDirectoryCreated;
        try
        {
            // Act
            using var content = new MemoryStream("second"u8.ToArray());
            exception = await Record.ExceptionAsync(() => operation switch
            {
                "add" => storage.AddSubjectAsync("Added.json", CreateMotor("Added"), CancellationToken.None),
                "add into new folder" => storage.AddSubjectAsync("New/Added.json", CreateMotor("Added"), CancellationToken.None),
                "write blob" => storage.WriteBlobAsync("Notes.md", content, CancellationToken.None),
                "write blob in folder" => storage.WriteBlobAsync("Docs/Page.md", content, CancellationToken.None),
                "write new blob" => storage.WriteBlobAsync("New/Data.bin", content, CancellationToken.None),
                "save configuration" => storage.WriteConfigurationAsync(motor, CancellationToken.None),
                "save embedded subject" => page.WriteConfigurationAsync(page.Children["motor"], CancellationToken.None),
                "delete blob" => storage.DeleteBlobAsync("Docs/Page.md", CancellationToken.None),
                _ => storage.DeleteSubjectAsync(motor, CancellationToken.None)
            });
            isDirectoryCreated = Directory.Exists(StorageDirectory.FullName);
        }
        finally
        {
            PutStorageDirectoryBack(movedDirectory);
        }

        await storage.ReconcileAsync();

        // Assert: nothing of the tree or of the files has changed.
        Assert.IsType<IOException>(exception, exactMatch: false);
        Assert.False(isDirectoryCreated);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Docs", "Embedding.md", "Motor", "Notes.md"], storage.Children.Keys.Order());
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("first", contentBefore);
        Assert.Equal("first", Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]).Content);
        Assert.Equal("first", File.ReadAllText(GetFullPath("Docs/Page.md")));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("metadata")]
    public async Task WhenPathInFolderIsReadWhileStorageDirectoryIsGone_ThenReadFailsAndDirectoryIsNotCreated(string call)
    {
        // Arrange: the disk client creates the folder of a path it is asked about, and with it the directory.
        WriteFile("Docs/Page.md");
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        var movedDirectory = StorageDirectory.FullName + "-moved";
        Directory.Move(StorageDirectory.FullName, movedDirectory);

        Exception? exception;
        bool isDirectoryCreated;
        StorageStatus statusAfterPass;
        List<string> childrenAfterPass;
        try
        {
            // Act
            exception = await Record.ExceptionAsync(() => call == "read"
                ? storage.ReadBlobAsync("Docs/Page.md", CancellationToken.None)
                : storage.GetBlobMetadataAsync("Docs/Page.md", CancellationToken.None));
            isDirectoryCreated = Directory.Exists(StorageDirectory.FullName);
            await storage.ReconcileAsync();
            statusAfterPass = storage.Status;
            childrenAfterPass = storage.Children.Keys.Order().ToList();
        }
        finally
        {
            PutStorageDirectoryBack(movedDirectory);
        }

        // Assert
        Assert.IsType<IOException>(exception, exactMatch: false);
        Assert.False(isDirectoryCreated);
        Assert.Equal(StorageStatus.Error, statusAfterPass);
        Assert.Equal(["Docs", "Notes.md"], childrenAfterPass);
    }

    [Fact]
    public async Task WhenStorageIsInMemory_ThenPassNeedsNoDirectory()
    {
        // Arrange
        var storage = await ConnectAsync(configure: container =>
        {
            container.StorageType = "inmemory";
            container.ConnectionString = Path.Combine(StorageDirectory.FullName, "Missing");
        });
        using var content = new MemoryStream([1, 2, 3]);
        await storage.WriteBlobAsync("Data.bin", content, CancellationToken.None);

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.bin"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageIsStopped_ThenItNoLongerFollowsItsStorage()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });

        // Act
        await storage.StopAsync(CancellationToken.None);
        var armedTimerCount = timeProvider.ArmedTimerCount;
        WriteFile("Added.md");
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        // A pass that the clock started would be queued by now, and the worker runs it before this write.
        using var content = new MemoryStream([1, 2, 3]);
        await storage.WriteBlobAsync("Other.bin", content, CancellationToken.None);

        // Assert
        Assert.Equal(0, armedTimerCount);
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenStorageIsStoppedWhileItReconnects_ThenNewConnectionDoesNotFollowItsStorage()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Waits for the pass of the connection it replaces, and publishes the new connection only after that.
        var reconnect = storage.ConnectAsync(CancellationToken.None);

        // Act
        await storage.StopAsync(CancellationToken.None);
        gate.Release();
        await pass;
        await reconnect;

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
    }

    [Fact]
    public async Task WhenStorageIsStopped_ThenItsFileWatcherStops()
    {
        // Arrange: the file shows that the watcher of the storage worked until it was stopped.
        var stopped = await ConnectAsync(enableFileWatching: true);
        var watching = await ConnectAsync(enableFileWatching: true);
        WriteFile("Before.md");
        await AsyncTestHelpers.WaitUntilAsync(() => stopped.Children.ContainsKey("Before.md"), WatcherTimeout);

        // Act: the second file is handled a full quiet period after the first,
        // so by then every watcher that is still running has handled the first one.
        await stopped.StopAsync(CancellationToken.None);
        WriteFile("First.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("First.md"), WatcherTimeout);
        WriteFile("Second.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("Second.md"), WatcherTimeout);

        // Assert
        Assert.Equal(["Before.md"], stopped.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageIsStopped_ThenSubjectsCanStillBeWritten()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        WriteFile("Notes.md", "first");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var notes = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        await storage.StopAsync(CancellationToken.None);

        // Act
        motor.Name = "Changed after stop";
        var isConfigurationWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);
        using var content = new MemoryStream("second"u8.ToArray());
        await storage.WriteBlobAsync("Notes.md", content, CancellationToken.None);

        // Assert
        Assert.True(isConfigurationWritten);
        Assert.Contains("Changed after stop", File.ReadAllText(GetFullPath("Motor.json")));
        Assert.Equal("second", notes.Content);
    }

    [Fact]
    public async Task WhenStorageIsStartedAfterItWasStopped_ThenItFollowsItsStorageAgain()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        await storage.StopAsync(CancellationToken.None);

        // Act
        await storage.StartAsync(CancellationToken.None);
        await storage.ExecuteTask!;
        WriteFile("Added.md");
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Children.ContainsKey("Added.md"), WatcherTimeout);
    }

    [Fact]
    public async Task WhenStorageReconnects_ThenTriggerOfPreviousConnectionIsStopped()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        var armedTimerCountOfOneConnection = timeProvider.ArmedTimerCount;

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, armedTimerCountOfOneConnection);
        Assert.Equal(1, timeProvider.ArmedTimerCount);
    }

    [Fact]
    public async Task WhenStorageIsDisposed_ThenItsTriggerIsStopped()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        var armedTimerCountWhileConnected = timeProvider.ArmedTimerCount;

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(1, armedTimerCountWhileConnected);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
    }

    [Theory]
    [InlineData("Notes.md", true)]
    [InlineData("Docs/Notes.md", true)]
    [InlineData("Missing.md", false)]
    [InlineData("Missing/Notes.md", false)]
    [InlineData("Docs", false)]
    [InlineData("", false)]
    public async Task WhenBlobMetadataIsRequested_ThenItReflectsTheFileOnDisk(string path, bool exists)
    {
        // Arrange
        WriteFile("Notes.md", "12345");
        WriteFile("Docs/Notes.md", "12345");
        var storage = await ConnectAsync();

        // Act
        var metadata = await storage.GetBlobMetadataAsync(path, CancellationToken.None);

        // Assert
        Assert.Equal(exists, metadata != null);
        if (metadata != null)
        {
            Assert.Equal(5, metadata.Size);
            Assert.Equal(File.GetLastWriteTimeUtc(GetFullPath(path)), metadata.LastModifiedUtc);
        }
    }

    [Fact]
    public async Task WhenBlobMetadataIsRequestedFromInMemoryStorage_ThenItReflectsTheBlob()
    {
        // Arrange
        var storage = await ConnectAsync(configure: container => container.StorageType = "inmemory");
        using var content = new MemoryStream([1, 2, 3, 4, 5]);
        await storage.WriteBlobAsync("Docs/Notes.md", content, CancellationToken.None);

        // Act
        var metadata = await storage.GetBlobMetadataAsync("Docs/Notes.md", CancellationToken.None);
        var missing = await storage.GetBlobMetadataAsync("Docs/Missing.md", CancellationToken.None);

        // Assert
        Assert.Equal(5, metadata?.Size);
        Assert.NotNull(metadata?.LastModifiedUtc);
        Assert.Null(missing);
    }

    [Fact]
    public async Task WhenStorageIsScanned_ThenTemporaryFilesAreSkipped()
    {
        // Arrange
        WriteFile("Home.md");
        WriteFile("Home.md~");
        WriteFile("~Draft.md");
        WriteFile("Docs/Notes.md.tmp");
        WriteFile("Docs/Notes.md");
        WriteFile("Build.tmp/Output.md");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["Docs", "Home.md"], storage.Children.Keys.Order());
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Notes.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenStorageIsScanned_ThenHiddenFilesAreSkipped()
    {
        // Arrange
        WriteFile(".DS_Store");
        WriteFile("._Home.md");
        WriteFile(".idea/workspace.xml");
        WriteFile("Docs/.hidden.md");
        WriteFile("Docs/Readme.md");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenNewFileIsSavedViaTempFileAndRename_ThenWatcherAddsFile()
    {
        // Arrange
        var storage = await ConnectAsync(enableFileWatching: true);

        // Act
        WriteFile("Notes.md.tmp");
        File.Move(GetFullPath("Notes.md.tmp"), GetFullPath("Notes.md"));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Children.ContainsKey("Notes.md"), WatcherTimeout);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenRegisteredFileIsRecreatedAndDeletedAtOnce_ThenWatcherRemovesFile()
    {
        // Arrange
        WriteFile("Home.md");
        WriteFile("Other.md");
        var storage = await ConnectAsync(enableFileWatching: true);

        // Act
        File.Delete(GetFullPath("Home.md"));
        WriteFile("Home.md");
        File.Delete(GetFullPath("Home.md"));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.Children.ContainsKey("Home.md"), WatcherTimeout);
        Assert.Equal(["Other.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenDirectoryWithFileIsCreated_ThenWatcherAddsFolderAndFile()
    {
        // Arrange
        var storage = await ConnectAsync(enableFileWatching: true);

        // Act
        WriteFile("Docs/Notes.md");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => storage.Children.GetValueOrDefault("Docs") is VirtualFolder folder && folder.Children.ContainsKey("Notes.md"),
            WatcherTimeout);
    }

    // What the file watcher logs within its start, once the file system watches. Nothing else shows that point.
    private const string WatcherStartedMessage = "File watching enabled";

    // What a pass of the trigger logs when it fails. Nothing else shows that such a pass has ended.
    private const string FailedPassMessage = "Failed to reconcile the storage";

    /// <summary>
    /// Connects a storage with a periodic pass every 300 seconds and lets that pass stall at its listing until
    /// it is given up. Its retry then runs while the listing has not returned, and is refused.
    /// </summary>
    private async Task<StalledStorage> StallPeriodicPassUntilItsRetryIsRefusedAsync()
    {
        var timeProvider = new ManualTimeProvider();
        PausableBlobStorage? client = null;
        var failedPassCount = 0;
        var logger = new CallbackLogger((_, message) =>
        {
            if (message.StartsWith(FailedPassMessage, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref failedPassCount);
            }
        });
        var storage = CreateStorage(logger: logger, configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 300;
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
        });
        await storage.ConnectAsync(CancellationToken.None);
        var listingReached = client!.PauseNext(nameof(IBlobStorage.ListAsync));
        timeProvider.Advance(TimeSpan.FromSeconds(300));
        await listingReached;

        // The periodic timer and the timer of the limit, and then the periodic timer and the timer of the retry.
        await AsyncTestHelpers.WaitUntilAsync(() => timeProvider.ArmedTimerCount == 2);
        timeProvider.Advance(StorageCallTimeout.Limit);
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref failedPassCount) == 1 && timeProvider.ArmedTimerCount == 2);
        var callCountBeforeRetry = client.CallCount;
        timeProvider.Advance(ReconcileTrigger.MaximumDelay);
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref failedPassCount) == 2);

        return new StalledStorage(storage, client, timeProvider, callCountBeforeRetry, () => Volatile.Read(ref failedPassCount));
    }

    private sealed record StalledStorage(
        FluentStorageContainer Storage,
        PausableBlobStorage Client,
        ManualTimeProvider TimeProvider,
        int CallCountBeforeRetry,
        Func<int> GetFailedPassCount);

    /// <summary>
    /// Moves the storage directory back to where it was, in place of a directory that was created there meanwhile.
    /// </summary>
    private void PutStorageDirectoryBack(string movedDirectory)
    {
        if (Directory.Exists(StorageDirectory.FullName))
        {
            Directory.Delete(StorageDirectory.FullName, recursive: true);
        }

        Directory.Move(movedDirectory, StorageDirectory.FullName);
    }

    /// <summary>
    /// Waits for something that passes started by the trigger bring about, with a clock that only the test moves.
    /// </summary>
    private static Task WaitWhileClockRunsAsync(ManualTimeProvider timeProvider, Func<bool> condition)
        => AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                // A quiet period per poll: a storage call is given up after thirty of them, which no call on a local disk takes.
                timeProvider.Advance(ReconcileTrigger.QuietPeriod);
                return condition();
            },
            WatcherTimeout);

    private sealed class CallbackLogger(Action<LogLevel, string> onLog) : ILogger<FluentStorageContainer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => onLog(logLevel, formatter(state, exception));
    }
}
