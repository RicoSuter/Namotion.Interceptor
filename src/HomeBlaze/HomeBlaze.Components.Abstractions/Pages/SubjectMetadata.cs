namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// How a subject stored as a JSON file appears in navigation, read from the metadata file beside its file. A null
/// value is not set, and what the subject provides itself applies.
/// </summary>
/// <param name="NavigationTitle">The title in navigation.</param>
/// <param name="Icon">The icon name in navigation. The icon of the subject itself is unchanged.</param>
/// <param name="Location">Where a subject with a page appears in navigation.</param>
/// <param name="Alignment">The alignment in the top bar, only used for <see cref="NavigationLocation.AppBar"/>.</param>
public sealed record SubjectMetadata(
    string? NavigationTitle,
    string? Icon,
    NavigationLocation? Location,
    AppBarAlignment? Alignment)
{
    /// <summary>
    /// The name of the dynamic registry property that holds the metadata of a subject.
    /// </summary>
    public const string PropertyName = "HB:Metadata";
}
