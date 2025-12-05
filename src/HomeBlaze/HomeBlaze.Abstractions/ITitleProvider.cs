namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide a custom display title.
/// </summary>
public interface ITitleProvider
{
    /// <summary>
    /// Gets the display title for this subject.
    /// </summary>
    string? Title { get; }
}
