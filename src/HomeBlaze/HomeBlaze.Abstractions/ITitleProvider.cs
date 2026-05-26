using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide a display title.
/// </summary>
[SubjectAbstraction]
[Description("Subject that provides a display title.")]
public interface ITitleProvider
{
    /// <summary>
    /// Gets the display title for this subject.
    /// </summary>
    string? Title { get; }
}
