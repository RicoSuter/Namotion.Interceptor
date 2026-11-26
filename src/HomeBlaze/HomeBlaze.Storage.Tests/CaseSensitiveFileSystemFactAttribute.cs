using Xunit;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A fact that needs a file system that tells names apart that differ only in case, as Linux does. The default file
/// systems of macOS and Windows do not, so two such files are one file there and the test cannot set up its storage.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    public CaseSensitiveFileSystemFactAttribute()
    {
        if (!CaseSensitiveFileSystem.IsTempCaseSensitive)
        {
            Skip = CaseSensitiveFileSystem.SkipReason;
        }
    }
}

/// <summary>
/// A theory that needs a file system that tells names apart that differ only in case, see
/// <see cref="CaseSensitiveFileSystemFactAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CaseSensitiveFileSystemTheoryAttribute : TheoryAttribute
{
    public CaseSensitiveFileSystemTheoryAttribute()
    {
        if (!CaseSensitiveFileSystem.IsTempCaseSensitive)
        {
            Skip = CaseSensitiveFileSystem.SkipReason;
        }
    }
}

internal static class CaseSensitiveFileSystem
{
    public const string SkipReason = "Requires a case-sensitive file system.";

    private static readonly Lazy<bool> IsCaseSensitive = new(Probe);

    /// <summary>
    /// Whether the folder of temporary files, where the storage tests keep their files, tells names apart that differ only
    /// in case. Probed on the disk once: a volume of macOS can be case-sensitive, and a folder of Linux can be mounted
    /// from one that is not, so the operating system does not tell.
    /// </summary>
    public static bool IsTempCaseSensitive => IsCaseSensitive.Value;

    private static bool Probe()
    {
        var directory = Directory.CreateTempSubdirectory("homeblaze-case-probe-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "probe"), string.Empty);
            return !File.Exists(Path.Combine(directory.FullName, "PROBE"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
