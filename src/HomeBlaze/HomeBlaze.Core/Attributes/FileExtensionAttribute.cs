namespace HomeBlaze.Core.Attributes;

/// <summary>
/// Maps a file extension to an InterceptorSubject type.
/// Used by storage containers to determine which subject type to create for a file.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class FileExtensionAttribute : Attribute
{
    public string Extension { get; }

    /// <param name="extension">The file extension including the dot, e.g. ".md"</param>
    public FileExtensionAttribute(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            throw new ArgumentException("Extension cannot be null or empty", nameof(extension));

        if (!extension.StartsWith('.'))
            throw new ArgumentException("Extension must start with a dot", nameof(extension));

        Extension = extension.ToLowerInvariant();
    }
}
