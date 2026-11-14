namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// Where a write puts metadata: the metadata file, and the entry in it.
/// </summary>
/// <param name="FilePath">The path of the metadata file in the storage.</param>
/// <param name="Section">
/// Null for the own keys of the file, else <see cref="StorageMetadataFile.SubjectsKey"/> or
/// <see cref="StorageMetadataFile.PropertiesKey"/>.
/// </param>
/// <param name="Path">The path of the entry in the section as it is written; null for the own keys.</param>
/// <param name="IsContainer">True for the file of a folder or of the root of a storage, whose own keys hold <c>order</c>.</param>
internal readonly record struct StorageMetadataTarget(string FilePath, string? Section, string? Path, bool IsContainer);
