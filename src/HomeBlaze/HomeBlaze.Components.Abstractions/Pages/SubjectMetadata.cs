namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// How a subject stored as a JSON file appears in navigation, read from the metadata file beside its file. A null
/// value is not set, and what the subject provides itself applies.
/// </summary>
public sealed record SubjectMetadata
{
    /// <summary>The title in navigation.</summary>
    public string? NavigationTitle { get; init; }

    /// <summary>The icon name in navigation. The icon of the subject itself is unchanged.</summary>
    public string? Icon { get; init; }

    /// <summary>Where a subject with a page appears in navigation.</summary>
    public NavigationLocation? Location { get; init; }

    /// <summary>The alignment in the top bar, only used for <see cref="NavigationLocation.AppBar"/>.</summary>
    public AppBarAlignment? Alignment { get; init; }
}
