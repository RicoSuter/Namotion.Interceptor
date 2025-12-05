namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for items stored in a storage container (files, folders, etc.)
/// </summary>
public interface IStorageItem
{
    /// <summary>
    /// Gets the file or folder name including extension.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Gets the full path to the item.
    /// </summary>
    string FilePath { get; }
}
