namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// The type of decoration region.
/// </summary>
public enum DecorationRegionType
{
    /// <summary>
    /// An inline expression like {{ path }}.
    /// </summary>
    Expression,

    /// <summary>
    /// A subject block like ```subject(name) ... ```.
    /// </summary>
    SubjectBlock
}