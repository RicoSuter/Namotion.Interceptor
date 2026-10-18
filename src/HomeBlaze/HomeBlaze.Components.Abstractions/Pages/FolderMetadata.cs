namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// The metadata of a folder, read from the <c>.metadata.json</c> file in the folder. A null value is not set.
/// </summary>
public sealed record FolderMetadata
{
    /// <summary>The title of the folder everywhere. Without one, the name of the directory.</summary>
    public string? Title { get; init; }

    /// <summary>The title in navigation. Without one, the title.</summary>
    public string? NavigationTitle { get; init; }

    /// <summary>The icon name of the folder everywhere. Without one, <c>Folder</c>.</summary>
    public string? Icon { get; init; }
}
