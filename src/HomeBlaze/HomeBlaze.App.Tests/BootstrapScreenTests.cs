using System.Text.Json;

namespace HomeBlaze.App.Tests;

public class BootstrapScreenTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bootstrap-screen").FullName;
    private readonly List<ShellConnection> _opened = [];

    private string FilePath => Path.Combine(_directory, "app-settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private BootstrapScreen CreateScreen(FakeDesktopShell? shell = null) => new(
        new ShellSettingsStore(FilePath),
        shell ?? new FakeDesktopShell(null),
        connection =>
        {
            _opened.Add(connection);
            return Task.CompletedTask;
        });

    [Fact]
    public void WhenThePageIsOpened_ThenTheEmbeddedResourceIsFound()
    {
        // Arrange & Act
        using var stream = BootstrapScreen.OpenPage();
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        // Assert
        Assert.Contains("window.external.sendMessage", content);
    }

    [Fact]
    public async Task WhenAReadyMessageArrives_ThenTheStateListsNoConnections()
    {
        // Arrange
        var screen = CreateScreen();

        // Act
        var state = await screen.HandleMessageAsync("""{"type":"ready"}""");

        // Assert
        var connections = JsonDocument.Parse(state!).RootElement.GetProperty("connections");
        Assert.Equal(0, connections.GetArrayLength());
    }

    [Fact]
    public async Task WhenARemoteConnectionIsAdded_ThenItAppearsInTheState()
    {
        // Arrange
        var screen = CreateScreen();

        // Act
        var state = await screen.HandleMessageAsync("""{"type":"add-remote","url":"http://pi:8080/"}""");

        // Assert
        var connections = JsonDocument.Parse(state!).RootElement.GetProperty("connections");
        Assert.Equal(1, connections.GetArrayLength());
        Assert.Equal("http://pi:8080/", connections[0].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task WhenTheUrlIsNotHttp_ThenAnErrorIsReported()
    {
        // Arrange
        var screen = CreateScreen();

        // Act
        var state = await screen.HandleMessageAsync("""{"type":"add-remote","url":"ftp://x/"}""");

        // Assert
        var root = JsonDocument.Parse(state!).RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("error").GetString()));
        Assert.Equal(0, root.GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task WhenAConnectionIsRemoved_ThenItIsGoneFromTheState()
    {
        // Arrange
        var screen = CreateScreen();
        var added = await screen.HandleMessageAsync("""{"type":"add-remote","url":"http://pi:8080/"}""");
        var id = JsonDocument.Parse(added!).RootElement.GetProperty("connections")[0].GetProperty("id").GetString();

        // Act
        var state = await screen.HandleMessageAsync($$"""{"type":"remove","id":"{{id}}"}""");

        // Assert
        var connections = JsonDocument.Parse(state!).RootElement.GetProperty("connections");
        Assert.Equal(0, connections.GetArrayLength());
    }

    [Fact]
    public async Task WhenTheMessageIsNotJson_ThenNothingHappens()
    {
        // Arrange
        var screen = CreateScreen();

        // Act
        var result = await screen.HandleMessageAsync("not json");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenAFolderIsPicked_ThenAConnectionIsAddedForIt()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("picked-folder").FullName;
        try
        {
            var screen = CreateScreen(new FakeDesktopShell(folder));

            // Act
            var state = await screen.HandleMessageAsync("""{"type":"add-folder","kind":"MarkdownFolder"}""");

            // Assert
            var connections = JsonDocument.Parse(state!).RootElement.GetProperty("connections");
            Assert.Equal(1, connections.GetArrayLength());
            Assert.Contains(folder, connections[0].GetProperty("detail").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task WhenTheFolderDialogIsCanceled_ThenNoConnectionIsAdded()
    {
        // Arrange
        var screen = CreateScreen(new FakeDesktopShell(null));

        // Act
        var state = await screen.HandleMessageAsync("""{"type":"add-folder","kind":"MarkdownFolder"}""");

        // Assert
        var connections = JsonDocument.Parse(state!).RootElement.GetProperty("connections");
        Assert.Equal(0, connections.GetArrayLength());
    }

    private sealed class FakeDesktopShell(string? folderToReturn) : IDesktopShell
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult(folderToReturn);

        public void Navigate(string address)
        {
        }

        public ShellWindowState GetWindowState() => new();
    }
}
