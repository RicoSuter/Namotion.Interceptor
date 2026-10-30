using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeBlaze.App;

/// <summary>
/// The shell's settings screen: serves its page and turns its messages into settings changes.
/// </summary>
/// <remarks>
/// It is deliberately independent of any HomeBlaze instance. In remote mode the instance is on another
/// machine and cannot host this, and in embedded mode no instance is running yet when this screen is shown.
/// </remarks>
public sealed class BootstrapScreen
{
    /// <summary>Scheme the page is served under. Photino requires a registered custom scheme.</summary>
    public const string Scheme = "hbapp";

    /// <summary>Address the window loads to show this screen.</summary>
    public const string Address = $"{Scheme}://settings/";

    private readonly ShellSettingsStore _store;
    private readonly IDesktopShell _shell;
    private readonly Func<ShellConnection, Task> _open;

    private string? _error;

    public BootstrapScreen(
        ShellSettingsStore store,
        IDesktopShell shell,
        Func<ShellConnection, Task> open)
    {
        _store = store;
        _shell = shell;
        _open = open;
    }

    /// <summary>Reads the page out of the assembly.</summary>
    public static Stream OpenPage()
    {
        const string resourceName = "HomeBlaze.App.Resources.bootstrap.html";
        return typeof(BootstrapScreen).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{resourceName}' is missing.");
    }

    /// <summary>Shows a message on the screen the next time it renders.</summary>
    public void SetError(string? error) => _error = error;

    /// <summary>Handles one message from the page. Returns the JSON state to send back, or null.</summary>
    public async Task<string?> HandleMessageAsync(string rawMessage)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(rawMessage);
        }
        catch (JsonException)
        {
            return null;
        }

        var type = message?["type"]?.GetValue<string>();
        switch (type)
        {
            case "ready":
                break;

            case "add-remote":
                AddRemote(message!["url"]!.GetValue<string>());
                break;

            case "add-folder":
                await AddFolderAsync(message!["kind"]!.GetValue<string>());
                break;

            case "remove":
                Remove(message!["id"]!.GetValue<string>());
                break;

            case "open":
                await OpenAsync(message!["id"]!.GetValue<string>());
                return null;

            default:
                return null;
        }

        return BuildState();
    }

    private void AddRemote(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            _error = $"'{url}' is not an http or https address.";
            return;
        }

        Add(new ShellConnection
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = parsed.Host,
            Kind = ConnectionKind.Remote,
            Url = parsed.ToString()
        });
    }

    private async Task AddFolderAsync(string kind)
    {
        if (!Enum.TryParse<ConnectionKind>(kind, out var connectionKind) || connectionKind == ConnectionKind.Remote)
        {
            return;
        }

        var folder = await _shell.PickFolderAsync(connectionKind == ConnectionKind.FullDataFolder
            ? "Select a HomeBlaze data folder"
            : "Select a folder of files");

        if (folder is null)
        {
            return;
        }

        Add(new ShellConnection
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)),
            Kind = connectionKind,
            FolderPath = folder
        });
    }

    private void Add(ShellConnection connection)
    {
        var settings = _store.Load();
        _store.Save(settings with { Connections = [.. settings.Connections, connection] });
        _error = null;
    }

    private void Remove(string id)
    {
        var settings = _store.Load();
        _store.Save(settings with
        {
            Connections = [.. settings.Connections.Where(connection => connection.Id != id)],
            LastConnectionId = settings.LastConnectionId == id ? null : settings.LastConnectionId
        });
    }

    private async Task OpenAsync(string id)
    {
        var connection = _store.Load().Connections.FirstOrDefault(candidate => candidate.Id == id);
        if (connection is null)
        {
            return;
        }

        await _open(connection);
    }

    private string BuildState()
    {
        var settings = _store.Load();
        var connections = new JsonArray();

        foreach (var connection in settings.Connections)
        {
            connections.Add(new JsonObject
            {
                ["id"] = connection.Id,
                ["name"] = connection.Name,
                ["detail"] = connection.Kind switch
                {
                    ConnectionKind.Remote => connection.Url,
                    ConnectionKind.FullDataFolder => $"Data folder: {connection.FolderPath}",
                    _ => $"Files: {connection.FolderPath}"
                }
            });
        }

        return new JsonObject { ["connections"] = connections, ["error"] = _error }.ToJsonString();
    }
}
