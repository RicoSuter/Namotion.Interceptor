using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerFileEventTests : IDisposable
{
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-storage-");
    private readonly List<FluentStorageContainer> _storages = [];
    private ServiceProvider? _serviceProvider;

    public FluentStorageContainerFileEventTests()
    {
        GatedFile.Reset();
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Deleted)]
    public async Task WhenEventArrivesForUnregisteredFileOnDisk_ThenFileIsAdded(WatcherChangeTypes changeType)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Notes.md");

        // Act
        await storage.ProcessFileEventAsync(Event(changeType, "Notes.md"));

        // Assert
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Deleted)]
    public async Task WhenEventArrivesForRegisteredFileMissingOnDisk_ThenSubjectIsRemoved(WatcherChangeTypes changeType)
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        File.Delete(GetFullPath("Home.md"));

        // Act
        await storage.ProcessFileEventAsync(Event(changeType, "Home.md"));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenCreatedEventArrivesForMissingFile_ThenNoSubjectIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Home.md"));

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
        await storage.ProcessFileEventAsync(Renamed("Old.md", "New.md"));

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
        await storage.ProcessFileEventAsync(Renamed("notes.md", "Notes.md"));

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
        await storage.ProcessFileEventAsync(Renamed("docs", "Docs"));

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Readme.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenCreatedEventArrivesForRegisteredFile_ThenSubjectInTreeKeepsReceivingChanges()
    {
        // Arrange
        WriteFile("Home.md", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<MarkdownFile>(storage.Children["Home.md"]);

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Home.md"));
        WriteFile("Home.md", "second");
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Changed, "Home.md"));

        // Assert
        Assert.Same(file, storage.Children["Home.md"]);
        Assert.Equal("second", file.Content);
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("._Home.md")]
    [InlineData(".idea/workspace.xml")]
    [InlineData("Docs/.hidden.md")]
    public async Task WhenHiddenFileIsCreated_ThenItIsIgnored(string relativePath)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(relativePath);

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, relativePath));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenHiddenDirectoryIsCreated_ThenItIsIgnored()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(".idea/workspace.xml");

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, ".idea"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));
        WriteFile("Docs/Notes.md");

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs/Notes.md"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Docs"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Docs"));
        WriteFile("Docs/Readme.md");

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs/Readme.md"));

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
        await storage.ProcessFileEventAsync(Renamed("Docs", "Archive"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Both.md", "OnlyNew.md"], docs.Children.Keys.Order());
        Assert.Same(both, docs.Children["Both.md"]);
        Assert.Equal("new", both.Content);
    }

    [Fact]
    public async Task WhenCreatedEventArrivesForTrackedDirectory_ThenFolderAndSubjectsAreKept()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var docs = storage.Children["Docs"];
        var readme = ((VirtualFolder)docs).Children["Readme.md"];

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

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
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs.json"));
        Assert.Equal(["Docs"], storage.Children.Keys);
        File.Delete(GetFullPath("Docs.json"));

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Docs.json"));

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
        await storage.ResyncAsync();

        // Assert
        Assert.Equal(["Docs", "Kept.md", "New"], storage.Children.Keys.Order());
        Assert.Same(kept, storage.Children["Kept.md"]);
        Assert.Equal("second", kept.Content);
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Equal(["Added.md"], docs.Children.Keys);
        Assert.Equal(["Added.md"], Assert.IsType<VirtualFolder>(storage.Children["New"]).Children.Keys);
    }

    [Fact]
    public async Task WhenDeletedEventCasingDiffersFromTreeKey_ThenSubjectIsRemoved()
    {
        // Arrange
        WriteFile("notes.md");
        var storage = await ConnectAsync();
        File.Delete(GetFullPath("notes.md"));

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Notes.md"));

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
        await storage.ResyncAsync();

        // Assert
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("Changed in memory", motor.Name);
    }

    [Fact]
    public async Task WhenTwoEventsLoadSameNewFile_ThenSubjectHasContentOfLaterLoad()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated", "first");

        var firstGate = GatedFile.PauseNextLoad();
        var firstAdd = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Data.gated"));
        await firstGate.WhenReachedAsync();

        WriteFile("Data.gated", "second");
        var secondGate = GatedFile.PauseNextLoad();
        var secondAdd = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Changed, "Data.gated"));
        await secondGate.WhenReachedAsync();

        // Act
        firstGate.Release();
        await firstAdd;
        secondGate.Release();
        await secondAdd;

        // Assert
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        Assert.Equal("second", file.Content);
    }

    [Fact]
    public async Task WhenFileIsDeletedWhileItsSubjectLoads_ThenNoSubjectIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated");

        var gate = GatedFile.PauseNextLoad();
        var add = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Data.gated"));
        await gate.WhenReachedAsync();

        File.Delete(GetFullPath("Data.gated"));
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Data.gated"));

        // Act
        gate.Release();
        await add;

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenFileIsAddedWhileStorageIsResynchronized_ThenFileIsKept()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");

        var gate = GatedFile.PauseNextLoad();
        var resync = storage.ResyncAsync();
        await gate.WhenReachedAsync();

        WriteFile("Added.md");
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Added.md"));

        // Act
        gate.Release();
        await resync;

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
        var added = new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>()) { Name = "Added" };

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
        var added = new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>());

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
        var added = new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>());

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
        var added = new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>()) { Name = "Added" };

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
    [InlineData("Docs/Old~/Notes.md")]
    public async Task WhenFileIsCreatedInTemporaryDirectory_ThenItIsIgnored(string relativePath)
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile(relativePath);

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, relativePath));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenTemporaryDirectoryIsRenamedToRealName_ThenOnlyRealFolderExists()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Docs.tmp/Readme.md");
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs.tmp/Readme.md"));
        Directory.Move(GetFullPath("Docs.tmp"), GetFullPath("Docs"));

        // Act: the watcher reports a rename from a temp name as a creation of the new path.
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Docs"));

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

        // Act: the second file is handled a full coalesce window after the first,
        // so by then every watcher that is still running has handled the first one.
        WriteFile("First.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("First.md"), WatcherTimeout);
        WriteFile("Second.md");
        await AsyncTestHelpers.WaitUntilAsync(() => watching.Children.ContainsKey("Second.md"), WatcherTimeout);

        // Assert
        Assert.Empty(reconfigured.Children);
    }

    [Theory]
    [InlineData("Notes.md", true)]
    [InlineData("Docs/Notes.md", true)]
    [InlineData("Missing.md", false)]
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
            storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, path)))));

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
            storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Notes.md")))));

        WriteFile("Notes.md", "second");
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Changed, "Notes.md"));

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

    private async Task<FluentStorageContainer> ConnectAsync(bool enableFileWatching = false)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);
        typeProvider.AddAssembly(typeof(GatedFile).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The application's context, so that the Children setters run change tracking, registry and lifecycle code.
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();
        _serviceProvider = serviceProvider;

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = enableFileWatching
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        _storages.Add(storage);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private static string SerializeMotor(string name)
    {
        // Serialized with services of its own, so the subject is not part of the storage under test.
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();

        using var serviceProvider = services.BuildServiceProvider();
        var motor = new Samples.Motor(serviceProvider.GetRequiredService<IInterceptorSubjectContext>()) { Name = name };
        return serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>().Serialize(motor);
    }

    private string GetFullPath(string relativePath)
        => Path.Combine(_directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relativePath, string content = "content")
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private RenamedEventArgs Renamed(string oldName, string newName)
        => new(WatcherChangeTypes.Renamed, _directory.FullName, newName, oldName);

    private FileSystemEventArgs Event(WatcherChangeTypes changeType, string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return new FileSystemEventArgs(changeType, Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
    }

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _serviceProvider?.Dispose();
        _directory.Delete(recursive: true);
    }
}
