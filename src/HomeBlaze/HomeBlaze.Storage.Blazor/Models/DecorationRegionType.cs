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
    /// The head line of a block with its type name and arguments, together with its json fence.
    /// </summary>
    SubjectBlock
}