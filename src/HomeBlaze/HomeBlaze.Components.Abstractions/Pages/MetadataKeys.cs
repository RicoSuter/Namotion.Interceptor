namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// The keys that HomeBlaze reads in a metadata file: the <c>.metadata.json</c> of a folder and the
/// <c>X.metadata.json</c> beside <c>X.json</c>.
/// </summary>
public static class MetadataKeys
{
    /// <summary>The title of a folder everywhere.</summary>
    public const string Title = "title";

    /// <summary>The title in navigation.</summary>
    public const string NavigationTitle = "navTitle";

    /// <summary>The icon name, of a folder everywhere and of a subject in navigation.</summary>
    public const string Icon = "icon";

    /// <summary>Where a subject with a page appears in navigation, a name of <see cref="NavigationLocation"/>.</summary>
    public const string Location = "location";

    /// <summary>The side of the top bar of a subject, a name of <see cref="AppBarAlignment"/>.</summary>
    public const string Alignment = "alignment";

    /// <summary>The names of the children of a folder in the order they are shown.</summary>
    public const string Order = "order";
}
