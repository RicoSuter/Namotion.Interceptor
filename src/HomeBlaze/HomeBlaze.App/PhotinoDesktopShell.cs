using Photino.NET;

namespace HomeBlaze.App;

/// <summary><see cref="IDesktopShell"/> over a <see cref="PhotinoWindow"/>.</summary>
public sealed class PhotinoDesktopShell : IDesktopShell
{
    private readonly PhotinoWindow _window;

    private string _lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public PhotinoDesktopShell(PhotinoWindow window)
    {
        _window = window;
    }

    // Photino's web message handler runs on the window's message-loop thread, and ShowOpenFolderAsync needs
    // that same loop to pump its completion. Awaiting it from there hangs silently and uncatchably, which a
    // spike reproduced. Invoke with the synchronous dialog is the form that demonstrably opens a dialog.
    public async Task<string?> PickFolderAsync(string title)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _window.Invoke(() =>
        {
            try
            {
                completion.SetResult(_window.ShowOpenFolder(title, _lastFolder, false).FirstOrDefault());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        var folder = await completion.Task;
        if (folder is not null)
        {
            _lastFolder = folder;
        }

        return folder;
    }

    public void Navigate(string address) => _window.Load(new Uri(address));

    public ShellWindowState GetWindowState() => new()
    {
        Width = _window.Width,
        Height = _window.Height,
        Left = _window.Left,
        Top = _window.Top,
        FullScreen = _window.FullScreen
    };
}
