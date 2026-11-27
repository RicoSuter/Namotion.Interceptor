using System.Runtime.InteropServices;
using System.Text.Json;

namespace HomeBlaze.App.Tests;

public class WorkspaceResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("workspace-resolver").FullName;

    private string WorkspacesRoot => Path.Combine(_root, "Workspaces");

    private WorkspaceResolver CreateResolver(string? seedDirectory = "/opt/app/Seed")
        => new(WorkspacesRoot, seedDirectory);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void WhenTheFolderIsAFullDataFolder_ThenTheRootFileIsInsideIt()
    {
        // Arrange
        var folder = Path.Combine(_root, "instance");
        Directory.CreateDirectory(folder);

        // Act
        var resolved = CreateResolver().ResolveFullDataFolder(folder);

        // Assert
        Assert.Equal(Path.Combine(folder, "Root.json"), resolved.RootConfigurationFile);
        Assert.Equal(folder, resolved.DataDirectory);
    }

    [Fact]
    public void WhenTheFolderIsAFullDataFolder_ThenSeedingIsEnabled()
    {
        // Arrange
        var folder = Path.Combine(_root, "instance");
        Directory.CreateDirectory(folder);

        // Act
        var resolved = CreateResolver().ResolveFullDataFolder(folder);

        // Assert
        Assert.Equal("/opt/app/Seed", resolved.SeedDirectory);
    }

    [Fact]
    public void WhenTheDataFolderHasOtherFilesAndNoRootFile_ThenItIsRefused()
    {
        // Arrange
        var folder = Path.Combine(_root, "notes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Page.md"), "# Page");
        var resolver = CreateResolver();

        // Act & Assert
        // Accepting it would seed HomeBlaze's own files into the folder and delete its Files/Docs on start.
        var exception = Assert.Throws<InvalidOperationException>(() => resolver.ResolveFullDataFolder(folder));
        Assert.Contains("not empty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheDataFolderHasARootFile_ThenOtherFilesDoNotRefuseIt()
    {
        // Arrange
        var folder = Path.Combine(_root, "instance");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Root.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "Unrelated.md"), "# Unrelated");

        // Act
        var resolved = CreateResolver().ResolveFullDataFolder(folder);

        // Assert
        Assert.Equal(folder, resolved.DataDirectory);
    }

    [Fact]
    public void WhenAFolderIsRefusedAsADataFolder_ThenItIsStillAcceptedAsAMarkdownFolder()
    {
        // Arrange
        var folder = Path.Combine(_root, "notes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Page.md"), "# Page");
        var resolver = CreateResolver();

        // Act
        var resolved = resolver.ResolveMarkdownFolder(folder);

        // Assert
        // The refusal message points the user here, so this has to work for the same folder.
        Assert.NotEqual(folder, resolved.DataDirectory);
        Assert.Equal(string.Empty, resolved.SeedDirectory);
    }

    [Fact]
    public void WhenTheFolderIsAMarkdownFolder_ThenTheRootFileIsInAPrivateWorkspace()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);

        // Act
        var resolved = CreateResolver().ResolveMarkdownFolder(notes);

        // Assert
        Assert.StartsWith(WorkspacesRoot, resolved.DataDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(resolved.DataDirectory, "Root.json"), resolved.RootConfigurationFile);
        Assert.NotEqual(notes, resolved.DataDirectory);
    }

    [Fact]
    public void WhenTheFolderIsAMarkdownFolder_ThenSeedingIsSuppressed()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);

        // Act
        var resolved = CreateResolver().ResolveMarkdownFolder(notes);

        // Assert
        // Empty rather than null: DataDirectorySeeder guards on string.IsNullOrWhiteSpace, so an empty
        // value switches off both seeding and the documentation replacement.
        Assert.Equal(string.Empty, resolved.SeedDirectory);
    }

    [Fact]
    public void WhenTheFolderIsAMarkdownFolder_ThenTheGeneratedRootPointsAtItByAbsolutePath()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);

        // Act
        var resolved = CreateResolver().ResolveMarkdownFolder(notes);

        // Assert
        using var document = JsonDocument.Parse(File.ReadAllText(resolved.RootConfigurationFile));
        var rootObject = document.RootElement;
        Assert.Equal("HomeBlaze.Storage.FluentStorageContainer", rootObject.GetProperty("$type").GetString());
        Assert.Equal("disk", rootObject.GetProperty("storageType").GetString());
        Assert.Equal(notes, rootObject.GetProperty("connectionString").GetString());
        Assert.True(rootObject.GetProperty("enableFileWatching").GetBoolean());
    }

    [Fact]
    public void WhenTheSameMarkdownFolderIsResolvedTwice_ThenTheWorkspaceIsReused()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        var resolver = CreateResolver();

        // Act
        var first = resolver.ResolveMarkdownFolder(notes);
        var second = resolver.ResolveMarkdownFolder(notes);

        // Assert
        Assert.Equal(first.DataDirectory, second.DataDirectory);
    }

    [Fact]
    public void WhenTheMarkdownFolderHasATrailingSeparator_ThenTheWorkspaceIsTheSame()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        var resolver = CreateResolver();

        // Act
        var plain = resolver.ResolveMarkdownFolder(notes);
        var trailing = resolver.ResolveMarkdownFolder(notes + Path.DirectorySeparatorChar);

        // Assert
        Assert.Equal(plain.DataDirectory, trailing.DataDirectory);
    }

    [Fact]
    public void WhenTwoFoldersShareALeafName_ThenTheirWorkspacesDiffer()
    {
        // Arrange
        var first = Path.Combine(_root, "one", "notes");
        var second = Path.Combine(_root, "two", "notes");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var resolver = CreateResolver();

        // Act
        var firstResolved = resolver.ResolveMarkdownFolder(first);
        var secondResolved = resolver.ResolveMarkdownFolder(second);

        // Assert
        Assert.NotEqual(firstResolved.DataDirectory, secondResolved.DataDirectory);
    }

    [Fact]
    public void WhenResolvingAMarkdownFolder_ThenNothingIsWrittenIntoIt()
    {
        // Arrange
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "Page.md"), "# Page");
        var before = Directory.GetFileSystemEntries(notes, "*", SearchOption.AllDirectories).OrderBy(e => e).ToArray();

        // Act
        CreateResolver().ResolveMarkdownFolder(notes);

        // Assert
        var after = Directory.GetFileSystemEntries(notes, "*", SearchOption.AllDirectories).OrderBy(e => e).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public void WhenTheFolderDoesNotExist_ThenResolvingItThrows()
    {
        // Arrange
        var missing = Path.Combine(_root, "missing");
        var resolver = CreateResolver();

        // Act & Assert
        Assert.Throws<DirectoryNotFoundException>(() => resolver.ResolveMarkdownFolder(missing));
        Assert.Throws<DirectoryNotFoundException>(() => resolver.ResolveFullDataFolder(missing));
    }

    [Fact]
    public void WhenThePathDiffersOnlyByCase_ThenTheWorkspaceMatchesPlatformCaseSensitivity()
    {
        // Arrange
        var notes = Path.Combine(_root, "Notes");
        Directory.CreateDirectory(notes);
        var isCaseInsensitiveFileSystem =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // Act
        var original = WorkspaceResolver.GetWorkspaceId(notes);
        var lowered = WorkspaceResolver.GetWorkspaceId(Path.Combine(_root, "notes"));

        // Assert
        if (isCaseInsensitiveFileSystem)
        {
            Assert.Equal(original, lowered);
        }
        else
        {
            Assert.NotEqual(original, lowered);
        }
    }
}
