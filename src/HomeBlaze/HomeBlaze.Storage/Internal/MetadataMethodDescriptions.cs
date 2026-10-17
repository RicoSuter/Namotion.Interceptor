namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The descriptions of the methods through which agents read and change the order and metadata of a folder, shared by
/// the root of a storage and its folders.
/// </summary>
internal static class MetadataMethodDescriptions
{
    public const string GetOrder =
        "Gets the names of the children of this folder in the order in which they are shown in navigation and in the browser. A name is the key of the child in Children.";

    public const string SetOrder =
        "Sets the order in which the children of this folder are shown: the given names first, in the given order, then the other children in the order in which they are shown now. Each name must be the name of a child, as Get Order returns it, and may be given only once.";

    public const string GetMetadata =
        "Gets the metadata of this folder (Title, NavigationTitle, Icon; null when it has none, always null for the root of a storage) and the navigation metadata of each child stored as a JSON file (NavigationTitle, Icon, Location, Alignment; null when it has none), keyed by the name of the child. Set Metadata and Set Child Metadata replace every value, so read it first to keep the values that are not changed.";

    public const string SetMetadata =
        "Replaces the metadata of this folder. A value that is null or blank is removed. title: the title of the folder everywhere, without one the name of the folder. navigationTitle: the title in navigation, without one the title. icon: a MudBlazor icon name such as Devices, without one Folder.";

    public const string SetChildMetadata =
        "Replaces the navigation metadata of a child stored as a JSON file, given by its name as Get Metadata lists it. A value that is null or blank is removed. navigationTitle: the title in navigation. icon: a MudBlazor icon name for navigation, the icon of the subject itself is unchanged. location: where a subject with a page appears in navigation. alignment: the side of the top bar, only used for the location AppBar.";
}
