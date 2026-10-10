using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerFileEventTests : IDisposable
{
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-storage-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenChangedEventArrivesForUnregisteredFile_ThenFileIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Notes.md");

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Changed, "Notes.md"));

        // Assert
        Assert.True(storage.Children.ContainsKey("Notes.md"));
    }

    [Fact]
    public async Task WhenChangedEventArrivesForDeletedFile_ThenSubjectIsRemoved()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        File.Delete(GetFullPath("Home.md"));

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Changed, "Home.md"));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenCreatedEventArrivesForDeletedFile_ThenNoSubjectIsAdded()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act
        await storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Home.md"));

        // Assert
        Assert.Empty(storage.Children);
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
        await storage.ProcessFileEventAsync(
            new RenamedEventArgs(WatcherChangeTypes.Renamed, _directory.FullName, "Archive", "Docs"));

        // Assert
        Assert.Equal(["Archive"], storage.Children.Keys);
        var archive = Assert.IsType<VirtualFolder>(storage.Children["Archive"]);
        Assert.Equal(["Readme.md"], archive.Children.Keys);
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
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = enableFileWatching
        };

        _storages.Add(storage);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private string GetFullPath(string relativePath)
        => Path.Combine(_directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relativePath, string content = "content")
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

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

        _directory.Delete(recursive: true);
    }
}
