using System.IO.Compression;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Extracts NuGet packages to a cache directory and resolves assembly paths.
/// </summary>
internal class PackageExtractor
{
    private static readonly global::NuGet.Frameworks.NuGetFramework CurrentFramework =
        global::NuGet.Frameworks.NuGetFramework.Parse(
            $"net{Environment.Version.Major}.{Environment.Version.Minor}");

    private const int MoveAttempts = 5;

    private readonly string _cacheDirectory;

    public PackageExtractor(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// Extracts a package stream to cache and returns assembly file paths for the best matching TFM.
    /// </summary>
    public IReadOnlyList<string> ExtractAndGetAssemblyPaths(
        string packageName, string packageVersion, Stream packageStream)
    {
        var packagePath = ExtractToCache(packageName, packageVersion, packageStream);
        return GetAssemblyPaths(packagePath);
    }

    /// <summary>
    /// Gets assembly paths from an already-extracted package cache directory.
    /// </summary>
    public IReadOnlyList<string> GetAssemblyPaths(string packagePath)
    {
        var libPath = Path.Combine(packagePath, "lib");
        if (!Directory.Exists(libPath))
        {
            return [];
        }

        var availableFrameworks = Directory.GetDirectories(libPath)
            .Select(directory => (
                Path: directory,
                Framework: global::NuGet.Frameworks.NuGetFramework.Parse(
                    Path.GetFileName(directory))))
            .ToList();

        var reducer = new global::NuGet.Frameworks.FrameworkReducer();
        var nearest = reducer.GetNearest(
            CurrentFramework,
            availableFrameworks.Select(entry => entry.Framework));

        if (nearest != null)
        {
            var matchingFolder = availableFrameworks.First(
                entry => entry.Framework.Equals(nearest));
            return Directory.GetFiles(matchingFolder.Path, "*.dll");
        }

        return [];
    }

    /// <summary>
    /// Gets the cache path for a package. Returns null if not cached.
    /// </summary>
    public string? GetCachedPackagePath(string packageName, string packageVersion)
    {
        var packagePath = Path.Combine(_cacheDirectory, packageName, packageVersion);
        return Directory.Exists(packagePath) ? packagePath : null;
    }

    private string ExtractToCache(string packageName, string packageVersion, Stream stream)
    {
        var packagePath = Path.Combine(_cacheDirectory, packageName, packageVersion);
        if (Directory.Exists(packagePath))
        {
            return packagePath;
        }

        // Extracted beside the target and moved into place, so a concurrent or interrupted extraction never
        // exposes a partial package.
        var temporaryPath = $"{packagePath}.extracting-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            ZipFile.ExtractToDirectory(stream, temporaryPath);
            MoveIntoPlace(temporaryPath, packagePath);
        }
        finally
        {
            // Best effort: failing here would mask the outcome of the extraction.
            try
            {
                if (Directory.Exists(temporaryPath))
                {
                    Directory.Delete(temporaryPath, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return packagePath;
    }

    private static void MoveIntoPlace(string temporaryPath, string packagePath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(temporaryPath, packagePath);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(packagePath))
                {
                    // Another extraction of the same package completed first.
                    return;
                }

                if (attempt == MoveAttempts)
                {
                    throw;
                }

                // On Windows, virus scanners and indexers briefly hold freshly extracted files open, which
                // blocks renaming their folder.
                Thread.Sleep(attempt * 100);
            }
        }
    }
}
