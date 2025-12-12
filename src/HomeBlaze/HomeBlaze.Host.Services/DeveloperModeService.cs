namespace HomeBlaze.Host.Services;

/// <summary>
/// Service for managing developer mode state across the HomeBlaze application.
/// When enabled, tracking components display debug information.
/// </summary>
public class DeveloperModeService
{
    /// <summary>
    /// Gets whether developer mode is currently enabled.
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Event raised when developer mode state changes.
    /// </summary>
    public event Action? OnChange;

    /// <summary>
    /// Toggles developer mode on/off.
    /// </summary>
    public void Toggle() => SetEnabled(!IsEnabled);

    /// <summary>
    /// Sets developer mode to a specific state.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (IsEnabled != enabled)
        {
            IsEnabled = enabled;
            OnChange?.Invoke();
        }
    }
}
