using System.Text.Json;

namespace HomeBlaze.App.Tests;

public class BootstrapScreenTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bootstrap-screen").FullName;
    private readonly List<ShellConnection> _opened = [];
    private int _closeCount;

    private string FilePath => Path.Combine(_directory, "app-settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private BootstrapScreen CreateScreen(FakeDesktopShell? shell = null) => new(
        new ShellSettingsStore(FilePath),
        shell ?? new FakeDesktopShell(null),
        connection =>
        {
            _opened.Add(connection);
            return Task.CompletedTask;
        },
        () =>
        {
            _closeCount++;
            return Task.CompletedTask;
        });

    private static async Task<string> AddRemoteAsync(BootstrapScreen screen)
    {
        var state = await screen.HandleMessageAsync("""{"type":"add-remote","url":"http://pi:8080/"}""");
        return JsonDocument.Parse(state!).RootElement.GetProperty("connections")[0].GetProperty("id").GetString()!;
    }

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

    [Fact]
    public async Task WhenAConnectionIsActive_ThenTheStateNamesIt()
    {
        // Arrange
        var screen = CreateScreen();
        var id = await AddRemoteAsync(screen);
        screen.SetActiveConnection(id, "http://pi:8080/");

        // Act
        var state = await screen.HandleMessageAsync("""{"type":"ready"}""");

        // Assert
        Assert.Equal(id, JsonDocument.Parse(state!).RootElement.GetProperty("activeId").GetString());
    }

    [Fact]
    public async Task WhenBackArrives_ThenTheWindowReturnsToTheActiveConnection()
    {
        // Arrange
        var shell = new FakeDesktopShell(null);
        var screen = CreateScreen(shell);
        var id = await AddRemoteAsync(screen);
        screen.SetActiveConnection(id, "http://127.0.0.1:5000/");

        // Act
        await screen.HandleMessageAsync("""{"type":"back"}""");

        // Assert
        Assert.Equal(["http://127.0.0.1:5000/"], shell.Navigations);
    }

    [Fact]
    public async Task WhenBackArrivesWithNoActiveConnection_ThenTheWindowStays()
    {
        // Arrange
        var shell = new FakeDesktopShell(null);
        var screen = CreateScreen(shell);
        var id = await AddRemoteAsync(screen);
        screen.SetActiveConnection(id, "http://127.0.0.1:5000/");
        screen.ClearActiveConnection();

        // Act
        await screen.HandleMessageAsync("""{"type":"back"}""");

        // Assert
        Assert.Empty(shell.Navigations);
    }

    [Fact]
    public async Task WhenTheActiveConnectionIsOpened_ThenTheWindowReturnsWithoutReopeningIt()
    {
        // Arrange
        var shell = new FakeDesktopShell(null);
        var screen = CreateScreen(shell);
        var id = await AddRemoteAsync(screen);
        screen.SetActiveConnection(id, "http://127.0.0.1:5000/");

        // Act
        await screen.HandleMessageAsync($$"""{"type":"open","id":"{{id}}"}""");

        // Assert
        Assert.Empty(_opened);
        Assert.Equal(["http://127.0.0.1:5000/"], shell.Navigations);
    }

    [Fact]
    public async Task WhenAnotherConnectionIsOpened_ThenItIsOpened()
    {
        // Arrange
        var screen = CreateScreen();
        var activeId = await AddRemoteAsync(screen);
        var otherState = await screen.HandleMessageAsync("""{"type":"add-remote","url":"http://other:8080/"}""");
        var otherId = JsonDocument.Parse(otherState!).RootElement.GetProperty("connections")[1].GetProperty("id").GetString();
        screen.SetActiveConnection(activeId, "http://pi:8080/");

        // Act
        await screen.HandleMessageAsync($$"""{"type":"open","id":"{{otherId}}"}""");

        // Assert
        Assert.Equal(otherId, Assert.Single(_opened).Id);
    }

    [Fact]
    public async Task WhenTheActiveConnectionIsRemoved_ThenItIsClosed()
    {
        // Arrange
        var screen = CreateScreen();
        var id = await AddRemoteAsync(screen);
        screen.SetActiveConnection(id, "http://pi:8080/");

        // Act
        await screen.HandleMessageAsync($$"""{"type":"remove","id":"{{id}}"}""");

        // Assert
        Assert.Equal(1, _closeCount);
    }

    [Fact]
    public async Task WhenAnInactiveConnectionIsRemoved_ThenTheActiveOneStaysOpen()
    {
        // Arrange
        var screen = CreateScreen();
        var activeId = await AddRemoteAsync(screen);
        var otherState = await screen.HandleMessageAsync("""{"type":"add-remote","url":"http://other:8080/"}""");
        var otherId = JsonDocument.Parse(otherState!).RootElement.GetProperty("connections")[1].GetProperty("id").GetString();
        screen.SetActiveConnection(activeId, "http://pi:8080/");

        // Act
        var state = await screen.HandleMessageAsync($$"""{"type":"remove","id":"{{otherId}}"}""");

        // Assert
        Assert.Equal(0, _closeCount);
        Assert.Equal(activeId, JsonDocument.Parse(state!).RootElement.GetProperty("activeId").GetString());
    }

    private sealed class FakeDesktopShell(string? folderToReturn) : IDesktopShell
    {
        public List<string> Navigations { get; } = [];

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(folderToReturn);

        public void Navigate(string address) => Navigations.Add(address);

        public ShellWindowState GetWindowState() => new();
    }
}
