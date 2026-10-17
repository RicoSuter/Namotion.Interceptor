namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// The metadata of a folder, read from the <c>.metadata.json</c> file in the folder. A null value is not set.
/// </summary>
/// <param name="Title">The title of the folder everywhere. Without one, the name of the directory.</param>
/// <param name="NavigationTitle">The title in navigation. Without one, the title.</param>
/// <param name="Icon">The icon name of the folder everywhere. Without one, <c>Folder</c>.</param>
public sealed record FolderMetadata(
    string? Title,
    string? NavigationTitle,
    string? Icon);
