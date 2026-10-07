namespace HomeBlaze.Services;

/// <summary>
/// Fills an empty instance data directory with the shipped defaults on first start.
/// </summary>
public static class DataDirectorySeeder
{
    /// <summary>
    /// File name of the seed directory's root configuration file, matched case-insensitively and only directly
    /// inside the seed directory (not in subfolders). It is copied to the configured root configuration path,
    /// regardless of that path's own file name.
    /// </summary>
    public const string SeedRootFileName = "Root.json";

    /// <summary>
    /// Copies the seed directory into the folder of <paramref name="rootConfigurationPath"/> when that file does not exist
    /// and <paramref name="seedDirectory"/> is set and exists. Existing files are never overwritten. The seed's top-level
    /// <see cref="SeedRootFileName"/> is copied to <paramref name="rootConfigurationPath"/> itself; all other files keep
    /// their relative path under the data directory.
    /// </summary>
    /// <returns>The number of copied files; zero when nothing was seeded.</returns>
    public static int SeedIfMissing(string rootConfigurationPath, string? seedDirectory)
    {
        if (string.IsNullOrWhiteSpace(seedDirectory) ||
            File.Exists(rootConfigurationPath) ||
            !Directory.Exists(seedDirectory))
        {
            return 0;
        }

        var sourceDirectory = Path.GetFullPath(seedDirectory);
        var fullRootConfigurationPath = Path.GetFullPath(rootConfigurationPath);
        var dataDirectory = Path.GetDirectoryName(fullRootConfigurationPath)!;

        // The root file is copied last: its presence is what disables seeding, so an interrupted
        // copy must not leave a root file behind that blocks the next attempt.
        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(file => IsSeedRootFile(sourceDirectory, file));

        var copiedFileCount = 0;
        foreach (var sourceFile in sourceFiles)
        {
            var isRootSource = IsSeedRootFile(sourceDirectory, sourceFile);
            var targetFile = isRootSource
                ? fullRootConfigurationPath
                : Path.Combine(dataDirectory, Path.GetRelativePath(sourceDirectory, sourceFile));

            if (File.Exists(targetFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            if (isRootSource)
            {
                // Written atomically: copying straight to the target could leave a truncated root
                // file if interrupted, which would wrongly look like seeding already completed.
                var temporaryFile = targetFile + ".seeding";
                File.Copy(sourceFile, temporaryFile, overwrite: true);
                File.Move(temporaryFile, targetFile);
            }
            else
            {
                File.Copy(sourceFile, targetFile);
            }

            copiedFileCount++;
        }

        return copiedFileCount;
    }

    private static bool IsSeedRootFile(string sourceDirectory, string sourceFile) =>
        string.Equals(Path.GetRelativePath(sourceDirectory, sourceFile), SeedRootFileName, StringComparison.OrdinalIgnoreCase);
}
