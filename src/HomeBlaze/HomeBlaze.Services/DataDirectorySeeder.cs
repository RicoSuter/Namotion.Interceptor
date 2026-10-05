namespace HomeBlaze.Services;

/// <summary>
/// Fills an empty instance data directory with the shipped defaults on first start.
/// </summary>
public static class DataDirectorySeeder
{
    /// <summary>
    /// Copies the seed directory into the folder of <paramref name="rootConfigurationPath"/> when that file does not exist
    /// and <paramref name="seedDirectory"/> is set and exists. Existing files are never overwritten.
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
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(rootConfigurationPath))!;
        var rootFileName = Path.GetFileName(rootConfigurationPath);

        // The root file is copied last: its presence is what disables seeding, so an interrupted
        // copy must not leave a root file behind that blocks the next attempt.
        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(sourceDirectory, file) == rootFileName);

        var copiedFileCount = 0;
        foreach (var sourceFile in sourceFiles)
        {
            var targetFile = Path.Combine(dataDirectory, Path.GetRelativePath(sourceDirectory, sourceFile));
            if (File.Exists(targetFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile);
            copiedFileCount++;
        }

        return copiedFileCount;
    }
}
