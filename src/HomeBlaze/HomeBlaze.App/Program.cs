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

    /// <summary>
    /// Serializes everything that reads or replaces <see cref="_host"/> and <see cref="_workspaceLock"/>.
    /// </summary>
    /// <remarks>
    /// A start suspends for seconds inside the host's own startup while the message loop keeps pumping, so a
    /// second Open can arrive mid-flight. Without this gate it would dispose a controller whose host is not
    /// published yet, which is a no-op, release the lock, and then start a second host on the same data
    /// directory while the first keeps running unreachable and never stops.
    /// </remarks>
    private static readonly SemaphoreSlim ConnectionGate = new(1, 1);

    /// <summary>
    /// Rolling log file for the embedded host. Deliberately under LocalApplicationData rather than beside
    /// the settings file: logs are machine-local and regenerable, and ApplicationData is the roaming profile
    /// on Windows, where rotating log files would bloat the profile and slow sign-in. This puts them next to
    /// the workspaces, which are machine-local for the same reason.
    /// </summary>
    private static string DefaultLogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeBlaze",
        "Logs",
        "homeblaze-{Date}.log");

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

        if (settings.Window.Left is null || settings.Window.Top is null)
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

        // Both of these need the native window to exist, so they run from the created handler rather than
        // here. Registered once, because the handler is a single delegate property rather than an event.
        var restoredLocation = settings.Window.Left is { } left && settings.Window.Top is { } top
            ? new System.Drawing.Point(left, top)
            : (System.Drawing.Point?)null;

        _window.RegisterWindowCreatedHandler((_, _) =>
        {
            if (restoredLocation is { } location)
            {
                // Not called before the window exists: on macOS 13 and earlier MoveTo reads MainMonitor,
                // which throws until then, so a saved position would make every later start fail outright.
                _window.MoveTo(location.X, location.Y, allowOutsideWorkArea: false);
            }

            if (last is not null)
            {
                _ = OpenConnectionAsync(last);
            }
        });

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
            if (message.StartsWith(ExternalLinks.MessagePrefix, StringComparison.Ordinal))
            {
                // A rejected URL is dropped, and a failed open is traced inside OpenInBackground, so neither
                // reaches the catch below, which would throw the user out of the instance onto settings.
                if (ExternalLinks.TryParseMessage(message, out var uri))
                {
                    ExternalLinks.OpenInBackground(uri);
                }

                return;
            }

            if (message == "shell:settings")
            {
                await ConnectionGate.WaitAsync();
                try
                {
                    await StopHostAsync();
                }
                finally
                {
                    ConnectionGate.Release();
                }

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
        await ConnectionGate.WaitAsync();
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

                // Held locally until the start succeeds. Assigning the field through the out parameter would
                // publish a lock that a failure then leaves for someone else's StopHostAsync to release.
                if (!WorkspaceLock.TryAcquire(workspace.DataDirectory, out var acquiredLock))
                {
                    throw new InvalidOperationException(
                        $"'{folderPath}' is already open in another HomeBlaze window.");
                }

                var host = new EmbeddedHostController(DefaultLogFilePath);
                try
                {
                    address = await host.StartAsync(workspace, CancellationToken.None);
                }
                catch
                {
                    await host.DisposeAsync();
                    acquiredLock!.Dispose();
                    throw;
                }

                _host = host;
                _workspaceLock = acquiredLock;
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
        finally
        {
            ConnectionGate.Release();
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
        // A minimized window reports an off-screen position on Windows, around -32000, and MoveTo does not
        // clamp negative coordinates. Persisting it would reopen the window where nobody can see it.
        if (!_window.Minimized)
        {
            var settings = _store.Load();
            _store.Save(settings with { Window = _shell.GetWindowState() });
        }

        // Blocking here is acceptable only because the application is exiting: the host needs up to ten
        // seconds to stop the OPC UA server cleanly, and the process must not exit from under it.
        ConnectionGate.Wait();
        try
        {
            StopHostAsync().GetAwaiter().GetResult();
        }
        finally
        {
            ConnectionGate.Release();
        }

        // False lets the window close; true would cancel it.
        return false;
    }
}
