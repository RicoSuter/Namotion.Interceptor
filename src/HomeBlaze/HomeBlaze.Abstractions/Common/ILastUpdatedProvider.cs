using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Interface for subjects that track when they were last updated.
/// </summary>
[SubjectAbstraction]
[Description("Subject that tracks when it was last updated.")]
public interface ILastUpdatedProvider
{
    /// <summary>
    /// The timestamp of the last update, or null if never updated.
    /// </summary>
    DateTimeOffset? LastUpdated { get; }
}
