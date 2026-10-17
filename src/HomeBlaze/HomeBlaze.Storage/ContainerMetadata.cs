using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Storage;

/// <summary>
/// The metadata of a folder and of the subjects stored as JSON files in it.
/// </summary>
/// <param name="Metadata">The metadata of the folder itself, null when it has none or is the root of a storage.</param>
/// <param name="Children">
/// The navigation metadata of each child stored as a JSON file, keyed by its name, null for a child without metadata.
/// </param>
public sealed record ContainerMetadata(
    FolderMetadata? Metadata,
    IReadOnlyDictionary<string, SubjectMetadata?> Children);
