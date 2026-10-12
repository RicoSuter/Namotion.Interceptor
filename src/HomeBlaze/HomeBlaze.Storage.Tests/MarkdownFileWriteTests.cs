using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class MarkdownFileWriteTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-markdown-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenReplacingTheContent_ThenTheFileTheContentAndTheParsedChildrenAreUpdated()
    {
        // Arrange
        WriteFile("Notes.md", "# Old heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act
        await markdown.ReplaceContentAsync("# New heading\n\nBody", CancellationToken.None);

        // Assert
        Assert.Equal("# New heading\n\nBody", ReadFile("Notes.md"));
        Assert.Equal("# New heading\n\nBody", markdown.Content);
        Assert.Contains(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("New heading"));
        Assert.DoesNotContain(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("Old heading"));
    }

    [Fact]
    public async Task WhenTheNewContentExceedsTheSizeLimit_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync(new string('a', MarkdownFile.MaxContentSize + 1), CancellationToken.None));
        Assert.Contains("65536", exception.Message);
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheNewContentIsExactlyAtTheSizeLimit_ThenItIsWritten()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        var content = new string('a', MarkdownFile.MaxContentSize);

        // Act
        await markdown.ReplaceContentAsync(content, CancellationToken.None);

        // Assert
        Assert.Equal(content, ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenReplacingAUniquePassage_ThenOnlyThatPassageChanges()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading\n\nThe freezer is at {{ /Kitchen/Freezer/Temperature }}.\n\nEnd");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act
        await markdown.ReplaceTextAsync("The freezer is at", "The kitchen freezer is at", CancellationToken.None);

        // Assert
        var expected = "# Heading\n\nThe kitchen freezer is at {{ /Kitchen/Freezer/Temperature }}.\n\nEnd";
        Assert.Equal(expected, ReadFile("Notes.md"));
        Assert.Equal(expected, markdown.Content);
    }

    [Fact]
    public async Task WhenThePassageIsNotFound_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync("# Other", "# New", CancellationToken.None));
        Assert.Contains("was not found", exception.Message);
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Theory]
    [InlineData("one two one", "one")]
    [InlineData("aaa", "aa")]
    public async Task WhenThePassageOccursMoreThanOnce_ThenItIsRejectedAndTheFileIsUnchanged(string content, string oldText)
    {
        // Arrange
        WriteFile("Notes.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(oldText, "x", CancellationToken.None));
        Assert.Contains("more than once", exception.Message);
        Assert.Equal(content, ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenThePassageIsEmpty_ThenItIsRejected()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => markdown.ReplaceTextAsync("", "x", CancellationToken.None));
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheReplacementMakesTheFileExceedTheSizeLimit_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync("Heading", new string('a', MarkdownFile.MaxContentSize), CancellationToken.None));
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheFileChangedOnDiskSinceItWasLoaded_ThenThePassageIsReplacedInTheCurrentSource()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading\n\nfirst");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        WriteFile("Notes.md", "# Heading\n\nfirst\n\nadded by an editor");

        // Act
        await markdown.ReplaceTextAsync("first", "changed", CancellationToken.None);

        // Assert
        Assert.Equal("# Heading\n\nchanged\n\nadded by an editor", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheStorageIsNotLocal_ThenTheWriteOperationsAreRefusedWithoutWriting()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        var (parser, serializer) = CreateParserAndSerializer();
        var markdown = new MarkdownFile(storage.Object, "Notes.md", parser, serializer);

        // Act & Assert
        var replaceContent = await Assert.ThrowsAsync<InvalidOperationException>(
            () => markdown.ReplaceContentAsync("# New", CancellationToken.None));
        var replaceText = await Assert.ThrowsAsync<InvalidOperationException>(
            () => markdown.ReplaceTextAsync("# Old", "# New", CancellationToken.None));
        Assert.Contains("local", replaceContent.Message);
        Assert.Contains("local", replaceText.Message);
        Assert.False(markdown.IsLocal);
        storage.Verify(container => container.WriteBlobAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        storage.Verify(container => container.ReadBlobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenTheStorageTypeIsNotLocal_ThenCreatingAMarkdownFileIsRefused()
    {
        // Arrange
        var storage = await ConnectAsync();
        var folder = new VirtualFolder(storage, "Docs/");
        storage.StorageType = "azure-blob";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        Assert.False(storage.IsLocal);
        Assert.False(folder.IsLocal);
        Assert.False(File.Exists(GetFullPath("Notes.md")));
        Assert.False(File.Exists(GetFullPath("Docs/Notes.md")));
    }

    [Fact]
    public async Task WhenAFolderBelongsToAnotherStorage_ThenCreatingAMarkdownFileIsRefused()
    {
        // Arrange
        var folder = new VirtualFolder(Mock.Of<IStorageContainer>(), "Docs/");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        Assert.False(folder.IsLocal);
    }

    [Theory]
    [InlineData("disk", true)]
    [InlineData("filesystem", true)]
    [InlineData("inmemory", true)]
    [InlineData("azure-blob", false)]
    public void WhenCheckingTheStorageType_ThenOnlyDiskAndInMemoryStorageIsLocal(string storageType, bool expected)
    {
        // Arrange
        var storage = CreateStorage();

        // Act
        storage.StorageType = storageType;

        // Assert
        Assert.Equal(expected, storage.IsLocal);
        Assert.Equal(expected, new VirtualFolder(storage, "Docs/").IsLocal);
    }

    [Fact]
    public async Task WhenCreatingAMarkdownFileInTheRoot_ThenItIsWrittenAndAddedAsAPage()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act
        await storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None);

        // Assert
        Assert.Equal("# Notes", ReadFile("Notes.md"));
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        Assert.Equal("# Notes", markdown.Content);
        Assert.Equal("/Notes.md", markdown.FullPath);
        Assert.Contains(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("Notes"));
    }

    [Fact]
    public async Task WhenCreatingAMarkdownFileInAFolder_ThenItIsAddedToTheFolderAndCanBeEdited()
    {
        // Arrange
        WriteFile("Docs/Existing.md", "# Existing");
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await folder.CreateMarkdownFileAsync("Notes.MD", "# Notes", CancellationToken.None);
        var markdown = Assert.IsType<MarkdownFile>(Assert.IsType<VirtualFolder>(storage.Children["Docs"]).Children["Notes.MD"]);
        await markdown.ReplaceTextAsync("# Notes", "# Changed", CancellationToken.None);

        // Assert
        Assert.Equal("/Docs/Notes.MD", markdown.FullPath);
        Assert.Equal("# Changed", ReadFile("Docs/Notes.MD"));
        Assert.Equal("# Changed", markdown.Content);
        Assert.True(Assert.IsType<VirtualFolder>(storage.Children["Docs"]).Children.ContainsKey("Existing.md"));
    }

    [Fact]
    public async Task WhenTheFileAlreadyExists_ThenCreatingItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Docs/Notes.md", "# Original");
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Replaced", CancellationToken.None));
        Assert.Contains("already exists", exception.Message);
        Assert.Equal("# Original", ReadFile("Docs/Notes.md"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("notes")]
    [InlineData("notes.txt")]
    [InlineData("notes.json")]
    [InlineData("notes.markdown")]
    [InlineData(".md")]
    [InlineData(".hidden.md")]
    [InlineData("~notes.md")]
    [InlineData("notes.tmp.md")]
    [InlineData(" notes.md")]
    [InlineData("notes.md ")]
    [InlineData("notes.md.")]
    [InlineData("Docs/notes.md")]
    [InlineData("../notes.md")]
    [InlineData("..\\notes.md")]
    [InlineData("no:tes.md")]
    [InlineData("no*tes.md")]
    [InlineData("no\ntes.md")]
    [InlineData("no\u200Btes.md")]
    [InlineData("notes\u202Edm.txt.md")]
    public async Task WhenTheNameIsNotAPlainMarkdownFileName_ThenCreatingTheFileIsRejected(string name)
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateMarkdownFileAsync(name, "# Notes", CancellationToken.None));
        Assert.Empty(storage.Children);
        Assert.Empty(_directory.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task WhenTheContentOfANewFileExceedsTheSizeLimit_ThenCreatingItIsRejected()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateMarkdownFileAsync("Notes.md", new string('a', MarkdownFile.MaxContentSize + 1), CancellationToken.None));
        Assert.False(File.Exists(GetFullPath("Notes.md")));
    }

    [Fact]
    public async Task WhenTheStorageIsInMemory_ThenAMarkdownFileCanBeCreatedAndEdited()
    {
        // Arrange
        var storage = await ConnectAsync(storageType: "inmemory");

        // Act
        await storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None);
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        await markdown.ReplaceContentAsync("# Replaced", CancellationToken.None);

        // Assert
        Assert.Equal("# Replaced", markdown.Content);
    }

    [Fact]
    public async Task WhenPagesAndFoldersAreInTheGraph_ThenTheWriteOperationsAreRegisteredWithoutConfirmation()
    {
        // Arrange
        WriteFile("Docs/Notes.md", "# Notes");

        // Act
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var markdown = Assert.IsType<MarkdownFile>(folder.Children["Notes.md"]);

        // Assert
        AssertOperation(markdown, "ReplaceContent", "Replace Content", "content");
        AssertOperation(markdown, "ReplaceText", "Replace Text", "oldText", "newText");
        AssertOperation(folder, "CreateMarkdownFile", "Create Markdown File", "name", "content");
        AssertOperation(storage, "CreateMarkdownFile", "Create Markdown File", "name", "content");
    }

    private static void AssertOperation(IInterceptorSubject subject, string name, string title, params string[] parameterNames)
    {
        var method = Assert.IsType<MethodMetadata>(subject.TryGetRegisteredSubject()!.TryGetProperty(name)!.GetValue());
        Assert.Equal(MethodKind.Operation, method.Kind);
        Assert.Equal(title, method.Title);
        Assert.False(method.RequiresConfirmation);
        Assert.False(string.IsNullOrEmpty(method.Description));
        Assert.Equal(parameterNames, method.Parameters.Where(parameter => parameter.RequiresInput).Select(parameter => parameter.Name));
        Assert.All(method.Parameters.Where(parameter => parameter.RequiresInput), parameter => Assert.Equal(typeof(string), parameter.Type));
    }

    private FluentStorageContainer CreateStorage(string storageType = "disk")
    {
        var services = CreateServices(out var context);
        var serviceProvider = services.BuildServiceProvider();
        var storage = new FluentStorageContainer(
            serviceProvider.GetRequiredService<SubjectTypeRegistry>(),
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            StorageType = storageType,
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        _storages.Add(storage);
        return storage;
    }

    private async Task<FluentStorageContainer> ConnectAsync(string storageType = "disk")
    {
        var storage = CreateStorage(storageType);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private static ServiceCollection CreateServices(out IInterceptorSubjectContext context)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);

        var services = new ServiceCollection();
        context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        return services;
    }

    private static (MarkdownContentParser Parser, ConfigurableSubjectSerializer Serializer) CreateParserAndSerializer()
    {
        var serviceProvider = CreateServices(out _).BuildServiceProvider();
        return (serviceProvider.GetRequiredService<MarkdownContentParser>(), serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>());
    }

    private string GetFullPath(string relativePath)
        => Path.Combine(_directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private string ReadFile(string relativePath) => File.ReadAllText(GetFullPath(relativePath));

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }
}
