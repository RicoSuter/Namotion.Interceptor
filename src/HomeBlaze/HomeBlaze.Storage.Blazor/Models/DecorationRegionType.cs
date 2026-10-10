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
    /// A widget block such as :::Full.Type.Name key=value.
    /// </summary>
    SubjectBlock
}