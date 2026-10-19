namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Holds the row layout of the property panel that the browser of a circuit has chosen.
/// </summary>
public class PropertyRowLayoutService
{
    /// <summary>
    /// Gets the chosen row layout.
    /// </summary>
    public PropertyRowLayout Layout { get; private set; }

    /// <summary>
    /// Gets the value that a browser stores to remember the layout, which <see cref="Load"/> reads back.
    /// </summary>
    public string StoredValue => Layout.ToString();

    /// <summary>
    /// Event raised when the row layout changes.
    /// </summary>
    public event Action? OnChange;

    /// <summary>
    /// Sets the row layout.
    /// </summary>
    public void SetLayout(PropertyRowLayout layout)
    {
        if (Layout != layout)
        {
            Layout = layout;
            OnChange?.Invoke();
        }
    }

    /// <summary>
    /// Sets the row layout from a stored value. A missing or unknown value is <see cref="PropertyRowLayout.Automatic"/>.
    /// </summary>
    public void Load(string? storedValue) => SetLayout(storedValue switch
    {
        nameof(PropertyRowLayout.SideBySide) => PropertyRowLayout.SideBySide,
        nameof(PropertyRowLayout.Stacked) => PropertyRowLayout.Stacked,
        _ => PropertyRowLayout.Automatic
    });
}
