using Photino.NET;

namespace HomeBlaze.App;

internal static class Program
{
    private static ShellSettingsStore _store = null!;
    private static WorkspaceResolver _resolver = null!;
    private static PhotinoWindow _window = null!;
    private static PhotinoDesktopShell _shell = null!;
    private static BootstrapScreen _bootstrap = null!;
    private static EmbeddedHostController? _host;
    private static WorkspaceLock? _workspaceLock;

    [STAThread]
    private static void Main(string[] arguments)
    {
        _store = new ShellSettingsStore(ShellSettingsStore.DefaultFilePath);

        var seedDirectory = Path.Combine(AppContext.BaseDirectory, "Seed");
        _resolver = new WorkspaceResolver(
            WorkspaceResolver.DefaultWorkspacesRoot,
            Directory.Exists(seedDirectory) ? seedDirectory : null);

        var settings = _store.Load();

        _window = new PhotinoWindow()
            .SetTitle("HomeBlaze")
            .SetSize(settings.Window.Width, settings.Window.Height)
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(false);

        var iconFile = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
        if (File.Exists(iconFile))
        {
            _window.SetIconFile(iconFile);
        }

        if (settings.Window.Left is { } left && settings.Window.Top is { } top)
        {
            _window.MoveTo(left, top, allowOutsideWorkArea: false);
        }
        else
        {
            _window.Center();
        }

        if (settings.Window.FullScreen)
        {
            _window.SetFullScreen(true);
        }

        _shell = new PhotinoDesktopShell(_window);
        _bootstrap = new BootstrapScreen(_store, _shell, OpenConnectionAsync);

        _window.RegisterCustomSchemeHandler(BootstrapScreen.Scheme, HandleBootstrapRequest);
        _window.RegisterWebMessageReceivedHandler(OnWebMessageReceived);
        _window.RegisterWindowClosingHandler(OnWindowClosing);

        // --settings forces the bootstrap screen even when a connection would open automatically. It is the
        // recovery path when the page-side shortcut is unavailable, which is the case for a remote instance
        // older than the script that registers it.
        var forceSettings = arguments.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var last = forceSettings
            ? null
            : settings.Connections.FirstOrDefault(connection => connection.Id == settings.LastConnectionId);

        if (last is not null)
        {
            // Started from the created handler rather than here: the window does not exist until the message
            // loop runs, so navigating from this thread would race its creation.
            _window.RegisterWindowCreatedHandler((_, _) => _ = OpenConnectionAsync(last));
        }

        _window
            .Load(new Uri(BootstrapScreen.Address))
            .WaitForClose();
    }

    private static Stream HandleBootstrapRequest(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/html";
        return BootstrapScreen.OpenPage();
    }

    // Returns immediately: this fires on the window's message-loop thread, and blocking it would hang the
    // window and deadlock any dialog the handler goes on to open.
    private static void OnWebMessageReceived(object? sender, string message) => _ = HandleWebMessageAsync(message);

    private static async Task HandleWebMessageAsync(string message)
    {
        try
        {
            if (message == "shell:settings")
            {
                await StopHostAsync();
                _shell.Navigate(BootstrapScreen.Address);
                return;
            }

            var state = await _bootstrap.HandleMessageAsync(message);
            if (state is not null)
            {
                _window.SendWebMessage(state);
            }
        }
        catch (Exception exception)
        {
            _bootstrap.SetError(exception.Message);
            _shell.Navigate(BootstrapScreen.Address);
        }
    }

    private static async Task OpenConnectionAsync(ShellConnection connection)
    {
        try
        {
            await StopHostAsync();

            string address;
            if (connection.Kind == ConnectionKind.Remote)
            {
                address = connection.Url ?? throw new InvalidOperationException("The connection has no URL.");
            }
            else
            {
                var folderPath = connection.FolderPath
                    ?? throw new InvalidOperationException("The connection has no folder.");

                var workspace = connection.Kind == ConnectionKind.FullDataFolder
                    ? _resolver.ResolveFullDataFolder(folderPath)
                    : _resolver.ResolveMarkdownFolder(folderPath);

                if (!WorkspaceLock.TryAcquire(workspace.DataDirectory, out _workspaceLock))
                {
                    throw new InvalidOperationException(
                        $"'{folderPath}' is already open in another HomeBlaze window.");
                }

                _host = new EmbeddedHostController(Path.Combine(
                    Path.GetDirectoryName(ShellSettingsStore.DefaultFilePath)!,
                    "Logs",
                    "homeblaze-{Date}.log"));
                address = await _host.StartAsync(workspace, CancellationToken.None);
            }

            var settings = _store.Load();
            _store.Save(settings with { LastConnectionId = connection.Id });

            _bootstrap.SetError(null);
            _shell.Navigate(address);
        }
        catch (Exception exception)
        {
            await StopHostAsync();

            // A failed start must never leave a blank window, so the reason goes back to the one screen
            // that is always reachable.
            _bootstrap.SetError($"Could not open '{connection.Name}': {exception.Message}");
            _shell.Navigate(BootstrapScreen.Address);
        }
    }

    private static async Task StopHostAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }

        _workspaceLock?.Dispose();
        _workspaceLock = null;
    }

    private static bool OnWindowClosing(object sender, EventArgs e)
    {
        var settings = _store.Load();
        _store.Save(settings with { Window = _shell.GetWindowState() });

        // Blocking here is acceptable only because the application is exiting: the host needs up to ten
        // seconds to stop the OPC UA server cleanly, and the process must not exit from under it.
        StopHostAsync().GetAwaiter().GetResult();

        // False lets the window close; true would cancel it.
        return false;
    }
}
