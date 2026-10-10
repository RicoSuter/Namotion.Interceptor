using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
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
    public async Task WhenNamedPathDoesNotExist_ThenNoSubjectIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();

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

    [Fact]
    public async Task WhenUnchangedFileIsNamed_ThenSubjectInTreeKeepsReceivingChanges()
    {
        // Arrange
        WriteFile("Home.md", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<MarkdownFile>(storage.Children["Home.md"]);

        // Act
        await storage.ReconcileAsync(Named("Home.md"));
        WriteFile("Home.md", "second");
        await storage.ReconcileAsync(Named("Home.md"));

        // Assert
        Assert.Same(file, storage.Children["Home.md"]);
        Assert.Equal("second", file.Content);
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("._Home.md")]
    [InlineData(".idea/workspace.xml")]
    public async Task WhenHiddenFileIsCreated_ThenItIsIgnored(string relativePath)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(relativePath);

        // Act
        await storage.ReconcileAsync(Named(relativePath));

        // Assert
        Assert.Empty(storage.Children);
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
    public async Task WhenHiddenDirectoryIsCreated_ThenItIsIgnored()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(".idea/workspace.xml");

        // Act
        await storage.ReconcileAsync(Named(".idea"));

        // Assert
        Assert.Empty(storage.Children);
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
    public async Task WhenFileIsCreatedInNewDirectory_ThenFileAppearsInFolder()
    {
        // Arrange
        var storage = await ConnectAsync();
        Directory.CreateDirectory(GetFullPath("Docs"));
        await storage.ReconcileAsync(Named("Docs"));
        WriteFile("Docs/Notes.md");

        // Act
        await storage.ReconcileAsync(Named("Docs/Notes.md"));

        // Assert
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.True(folder.Children.ContainsKey("Notes.md"));
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
    public async Task WhenFileIsRecreatedAfterDirectoryWasDeleted_ThenFileAppearsAgain()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        await storage.ReconcileAsync(Named("Docs"));
        WriteFile("Docs/Readme.md");

        // Act
        await storage.ReconcileAsync(Named("Docs/Readme.md"));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
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
    public async Task WhenTrackedDirectoryIsNamed_ThenFolderAndSubjectsAreKept()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var docs = storage.Children["Docs"];
        var readme = ((VirtualFolder)docs).Children["Readme.md"];

        // Act
        await storage.ReconcileAsync(Named("Docs"));

        // Assert
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Same(readme, ((VirtualFolder)docs).Children["Readme.md"]);
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
    public async Task WhenFileIsGoneAndNamedInAnotherCasing_ThenSubjectIsRemoved()
    {
        // Arrange
        WriteFile("notes.md");
        var storage = await ConnectAsync();
        File.Delete(GetFullPath("notes.md"));

        // Act
        await storage.ReconcileAsync(Named("Notes.md"));

        // Assert
        Assert.Empty(storage.Children);
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

    [Theory]
    [InlineData("Build.tmp/Output.md")]
    [InlineData("~Backup/Notes.md")]
    public async Task WhenFileIsCreatedInTemporaryDirectory_ThenItIsIgnored(string relativePath)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(relativePath);

        // Act
        await storage.ReconcileAsync(Named(relativePath));

        // Assert
        Assert.Empty(storage.Children);
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
    public async Task WhenReconcileIntervalExceedsWhatTimerAccepts_ThenStorageConnectsAndPeriodicPassIsScheduled()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();

        // Act
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = int.MaxValue;
        });

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(1, timeProvider.ArmedTimerCount);
    }

    [Fact]
    public async Task WhenFileWatcherCannotStart_ThenConnectFailsAndNothingKeepsRunning()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var missingDirectory = Path.Combine(StorageDirectory.FullName, "Missing");

        // Act
        var exception = await Record.ExceptionAsync(() => ConnectAsync(enableFileWatching: true, configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ConnectionString = missingDirectory;
        }));

        // Assert
        Assert.NotNull(exception);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
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
        var reconnect = storage.ApplyConfigurationAsync(CancellationToken.None);

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
        // Arrange
        var stopped = await ConnectAsync(enableFileWatching: true);
        await stopped.StopAsync(CancellationToken.None);

        var watching = await ConnectAsync(enableFileWatching: true);

        // Act: the second file is handled a full quiet period after the first,
        // so by then every watcher that is still running has handled the first one.
        WriteFile("First.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("First.md"), WatcherTimeout);
        WriteFile("Second.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("Second.md"), WatcherTimeout);

        // Assert
        Assert.Empty(stopped.Children);
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
        await storage.ApplyConfigurationAsync(CancellationToken.None);

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
    public async Task WhenFilesAreCreatedConcurrently_ThenAllFilesAreAdded()
    {
        // Arrange
        const int fileCount = 100;
        var storage = await ConnectAsync();
        var paths = Enumerable.Range(0, fileCount)
            .Select(index => index % 2 == 0 ? $"File{index}.json" : $"Docs/File{index}.json")
            .ToArray();

        foreach (var path in paths)
        {
            WriteFile(path, "{}");
        }

        // Act
        await Task.WhenAll(paths.Select(path => Task.Run(() =>
            storage.ReconcileAsync(Named(path)))));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(fileCount / 2, docs.Children.Count);
        Assert.Equal(fileCount / 2 + 1, storage.Children.Count);
        Assert.All(docs.Children.Values, child => Assert.NotNull(child.TryGetRegisteredSubject()));
    }

    [Fact]
    public async Task WhenSameFileIsCreatedConcurrently_ThenSubjectInTreeKeepsReceivingChanges()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Notes.md", "first");

        // Act
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            storage.ReconcileAsync(Named("Notes.md")))));

        WriteFile("Notes.md", "second");
        await storage.ReconcileAsync(Named("Notes.md"));

        // Assert
        var file = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        Assert.Equal("second", file.Content);
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
}
