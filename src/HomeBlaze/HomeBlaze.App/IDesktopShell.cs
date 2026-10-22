namespace HomeBlaze.App;

/// <summary>Window and dialog operations the bootstrap screen drives.</summary>
public interface IDesktopShell
{
    /// <summary>Shows a native folder dialog and returns the chosen folder, or null when canceled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Points the window at an address.</summary>
    void Navigate(string address);

    /// <summary>Current window geometry, for persisting across restarts.</summary>
    ShellWindowState GetWindowState();
}
