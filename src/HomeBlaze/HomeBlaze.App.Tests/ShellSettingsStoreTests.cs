namespace HomeBlaze.App.Tests;

public class ShellSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("shell-settings").FullName;

    private string FilePath => Path.Combine(_directory, "app-settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WhenTheFileDoesNotExist_ThenDefaultsAreReturned()
    {
        // Arrange
        var store = new ShellSettingsStore(FilePath);

        // Act
        var settings = store.Load();

        // Assert
        Assert.Empty(settings.Connections);
        Assert.Null(settings.LastConnectionId);
        Assert.Equal(1280, settings.Window.Width);
    }

    [Fact]
    public void WhenSettingsAreSaved_ThenTheyRoundTrip()
    {
        // Arrange
        var store = new ShellSettingsStore(FilePath);
        var settings = new ShellSettings
        {
            Connections =
            [
                new ShellConnection { Id = "a", Name = "Notes", Kind = ConnectionKind.MarkdownFolder, FolderPath = "/tmp/notes" },
                new ShellConnection { Id = "b", Name = "Server", Kind = ConnectionKind.Remote, Url = "http://pi:8080/" }
            ],
            LastConnectionId = "b",
            Window = new ShellWindowState { Width = 1000, Height = 700, Left = 20, Top = 30, FullScreen = true }
        };

        // Act
        store.Save(settings);
        var loaded = new ShellSettingsStore(FilePath).Load();

        // Assert
        // Compared part by part on purpose. The generated record equality uses
        // EqualityComparer<IReadOnlyList<ShellConnection>>.Default for Connections, which is reference
        // equality, so whole-record equality can never hold across a round trip however correct the
        // contents are. Asserting the parts compares the contents element by element instead.
        Assert.Equal(settings.Connections, loaded.Connections);
        Assert.Equal(settings.LastConnectionId, loaded.LastConnectionId);
        Assert.Equal(settings.Window, loaded.Window);
    }

    [Fact]
    public void WhenTheFileIsCorrupt_ThenDefaultsAreReturnedInsteadOfThrowing()
    {
        // Arrange
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new ShellSettingsStore(FilePath);

        // Act
        var settings = store.Load();

        // Assert
        Assert.Empty(settings.Connections);
    }

    [Fact]
    public void WhenTheDirectoryIsMissing_ThenSavingCreatesIt()
    {
        // Arrange
        var nested = Path.Combine(_directory, "a", "b", "app-settings.json");
        var store = new ShellSettingsStore(nested);

        // Act
        store.Save(new ShellSettings { LastConnectionId = "x" });

        // Assert
        Assert.True(File.Exists(nested));
        Assert.Equal("x", new ShellSettingsStore(nested).Load().LastConnectionId);
    }
}
