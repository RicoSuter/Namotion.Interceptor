using System.IO.Compression;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Extracts NuGet packages to a cache directory and resolves assembly paths.
/// </summary>
internal class PackageExtractor
{
    private static readonly global::NuGet.Frameworks.NuGetFramework[] TargetFrameworkPriority =
    [
        global::NuGet.Frameworks.NuGetFramework.Parse("net10.0"),
        global::NuGet.Frameworks.NuGetFramework.Parse("net9.0"),
        global::NuGet.Frameworks.NuGetFramework.Parse("net8.0"),
        global::NuGet.Frameworks.NuGetFramework.Parse("net7.0"),
        global::NuGet.Frameworks.NuGetFramework.Parse("net6.0"),
        global::NuGet.Frameworks.NuGetFramework.Parse("netstandard2.1"),
        global::NuGet.Frameworks.NuGetFramework.Parse("netstandard2.0"),
    ];

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

        var reducer = new global::NuGet.Frameworks.FrameworkReducer();
        var availableFolders = Directory.GetDirectories(libPath)
            .Select(directory => new
            {
                Path = directory,
                Framework = global::NuGet.Frameworks.NuGetFramework.Parse(Path.GetFileName(directory))
            })
            .ToList();

        // Try each framework in priority order
        foreach (var targetFramework in TargetFrameworkPriority)
        {
            var match = availableFolders
                .FirstOrDefault(folder => folder.Framework.Equals(targetFramework));

            if (match != null)
            {
                return Directory.GetFiles(match.Path, "*.dll");
            }
        }

        // Fall back to framework reducer for compatibility matching
        var availableFrameworks = availableFolders.Select(folder => folder.Framework).ToList();
        var nearest = reducer.GetNearest(
            global::NuGet.Frameworks.NuGetFramework.Parse(
                $"net{Environment.Version.Major}.{Environment.Version.Minor}"),
            availableFrameworks);

        if (nearest != null)
        {
            var nearestFolder = availableFolders.First(folder => folder.Framework.Equals(nearest));
            return Directory.GetFiles(nearestFolder.Path, "*.dll");
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

    /// <summary>
    /// Reads the package version from a .nupkg file using NuGet SDK.
    /// </summary>
    public static string? GetVersionFromPackage(string nupkgPath)
    {
        using var reader = new global::NuGet.Packaging.PackageArchiveReader(nupkgPath);
        return reader.NuspecReader.GetVersion().ToNormalizedString();
    }

    private string ExtractToCache(string packageName, string packageVersion, Stream stream)
    {
        var packagePath = Path.Combine(_cacheDirectory, packageName, packageVersion);

        if (!Directory.Exists(packagePath))
        {
            Directory.CreateDirectory(packagePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(packagePath);
        }

        return packagePath;
    }
}
