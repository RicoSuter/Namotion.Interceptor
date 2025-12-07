namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide display information for UI.
/// </summary>
public interface IDisplaySubject
{
    /// <summary>
    /// Gets the display title for this subject.
    /// </summary>
    string? Title { get; }

    /// <summary>
    /// Gets the icon string for this subject.
    /// </summary>
    string? Icon { get; }
}
