namespace HomeBlaze.Abstractions;

/// <summary>
/// Provides the instance data directory: the folder that contains the root configuration file.
/// Relative paths in configuration resolve against it.
/// </summary>
public interface IDataDirectoryProvider
{
    /// <summary>
    /// Gets the full path of the instance data directory.
    /// </summary>
    string DataDirectory { get; }
}
