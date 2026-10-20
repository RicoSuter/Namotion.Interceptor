namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// The keys of metadata that HomeBlaze reads, see <see cref="IMetadataStore"/>. Metadata can hold other keys as well.
/// </summary>
public static class MetadataKeys
{
    /// <summary>The title of a folder everywhere.</summary>
    public const string Title = "title";

    /// <summary>The title in navigation.</summary>
    public const string NavigationTitle = "navTitle";

    /// <summary>The icon name, of a folder everywhere and of a subject in navigation.</summary>
    public const string Icon = "icon";

    /// <summary>Where a subject with a page appears in navigation, a name of <c>NavigationLocation</c>.</summary>
    public const string Location = "location";

    /// <summary>The side of the top bar of a subject, a name of <c>AppBarAlignment</c>.</summary>
    public const string Alignment = "alignment";

    /// <summary>The names of the children of a folder in the order they are shown.</summary>
    public const string Order = "order";

    /// <summary>Notes about a subject or property in Markdown, under the rules of <c>HomeBlaze.Storage.MetadataNotes</c>.</summary>
    public const string Notes = "notes";

    /// <summary>Links of a subject or property: objects with an optional <c>title</c> and exactly one of a <c>url</c> and a <c>path</c>.</summary>
    public const string Links = "links";

    /// <summary>Tags of a subject or property: at most 20 strings of up to 64 characters, unique without regard to case.</summary>
    public const string Tags = "tags";
}
