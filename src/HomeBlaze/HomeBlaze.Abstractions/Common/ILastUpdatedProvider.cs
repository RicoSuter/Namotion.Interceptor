namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Interface for subjects that track when they were last updated.
/// </summary>
public interface ILastUpdatedProvider
{
    /// <summary>
    /// The timestamp of the last update, or null if never updated.
    /// </summary>
    DateTimeOffset? LastUpdated { get; }
}
