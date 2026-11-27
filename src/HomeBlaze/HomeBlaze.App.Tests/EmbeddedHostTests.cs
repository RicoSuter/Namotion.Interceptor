using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.App.Tests;

[Trait("Category", "Integration")]
public class EmbeddedHostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("embedded-host").FullName;

    private WorkspaceResolver CreateResolver() => new(Path.Combine(_root, "Workspaces"), seedDirectory: null);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task WhenStartedOnAFullDataFolder_ThenItListensOnLoopback()
    {
        // Arrange
        var folder = CreateFullDataFolder("instance");
        await using var controller = new EmbeddedHostController();

        // Act
        var address = await controller.StartAsync(CreateResolver().ResolveFullDataFolder(folder), CancellationToken.None);

        // Assert
        Assert.StartsWith("http://127.0.0.1:", address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenStarted_ThenTheHealthEndpointAnswers()
    {
        // Arrange
        var folder = CreateFullDataFolder("instance");
        await using var controller = new EmbeddedHostController();
        var address = await controller.StartAsync(CreateResolver().ResolveFullDataFolder(folder), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"/health returned {response.StatusCode}");
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task WhenStarted_ThenStaticAssetsHaveContent(string path)
    {
        // Arrange
        var folder = CreateFullDataFolder("instance");
        await using var controller = new EmbeddedHostController();
        var address = await controller.StartAsync(CreateResolver().ResolveFullDataFolder(folder), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        // Act
        var response = await client.GetAsync(path);
        var content = await response.Content.ReadAsByteArrayAsync();

        // Assert
        // The endpoint answers 200 even when no file stands behind it, so only the length shows it was served.
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {response.StatusCode}");
        Assert.NotEmpty(content);
    }

    [Fact]
    public async Task WhenStartedOnAFullDataFolder_ThenTheDataDirectoryIsThatFolder()
    {
        // Arrange
        var folder = CreateFullDataFolder("instance");
        await using var controller = new EmbeddedHostController();
        await controller.StartAsync(CreateResolver().ResolveFullDataFolder(folder), CancellationToken.None);

        // Act
        // RootManager is registered as its concrete type and implements IDataDirectoryProvider;
        // the interface itself is not in the container.
        var dataDirectory = controller.Services!.GetRequiredService<RootManager>().DataDirectory;

        // Assert
        Assert.Equal(folder, dataDirectory);
    }

    [Fact]
    public async Task WhenStartedOnAMountedFolder_ThenTheFolderIsNotWrittenInto()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "Page.md"), "# Page");
        var resolved = CreateResolver().ResolveMarkdownFolder(notes);
        var before = Snapshot(notes);

        // Act
        await using (var controller = new EmbeddedHostController())
        {
            await controller.StartAsync(resolved, CancellationToken.None);
        }

        // Assert
        Assert.Equal(before, Snapshot(notes));
    }

    [Fact]
    public async Task WhenStartedOnAMountedFolder_ThenTheDataDirectoryIsThePrivateWorkspace()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        var resolved = CreateResolver().ResolveMarkdownFolder(notes);
        await using var controller = new EmbeddedHostController();
        await controller.StartAsync(resolved, CancellationToken.None);

        // Act
        var dataDirectory = controller.Services!.GetRequiredService<RootManager>().DataDirectory;

        // Assert
        Assert.Equal(resolved.DataDirectory, dataDirectory);
        Assert.NotEqual(notes, dataDirectory);
    }

    private string CreateFullDataFolder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "Root.json"),
            """{ "$type": "HomeBlaze.Storage.FluentStorageContainer", "storageType": "disk", "connectionString": "Files", "enableFileWatching": false }""");

        return folder;
    }

    private static string[] Snapshot(string directory)
        => [.. Directory
            .GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => File.Exists(entry)
                ? $"{Path.GetRelativePath(directory, entry)}:{new FileInfo(entry).Length}"
                : $"{Path.GetRelativePath(directory, entry)}/")
            .OrderBy(entry => entry, StringComparer.Ordinal)];
}
