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

        // Extracted into a unique sibling and moved into place, so neither a concurrent extraction of the
        // same package nor an interrupted one can leave a partial directory that later loads treat as complete.
        var temporaryPath = $"{packagePath}.extracting-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(temporaryPath);
            }

            try
            {
                Directory.Move(temporaryPath, packagePath);
            }
            catch (IOException) when (Directory.Exists(packagePath))
            {
                // Another extraction of the same package completed first; its copy is equivalent.
            }
        }
        finally
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
        }

        return packagePath;
    }
}
