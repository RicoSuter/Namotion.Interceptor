namespace HomeBlaze.Services;

/// <summary>
/// Fills an empty instance data directory with the shipped defaults on first start,
/// and replaces the documentation directory with the shipped one on every start.
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
    /// The documentation directory, relative to both the seed directory and the data directory, which
    /// <see cref="ReplaceDocumentation"/> replaces.
    /// </summary>
    public const string DocumentationDirectory = "Files/Docs";

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

    /// <summary>
    /// Replaces the documentation directory (<c>Files/Docs</c>) in the folder of <paramref name="rootConfigurationPath"/> with
    /// the one of <paramref name="seedDirectory"/>: the target is deleted with everything in it, then the seed's files are copied.
    /// Nothing is deleted or copied when <paramref name="seedDirectory"/> is not set or does not contain that directory.
    /// </summary>
    /// <returns>The number of copied files; zero when nothing was copied.</returns>
    public static int ReplaceDocumentation(string rootConfigurationPath, string? seedDirectory)
    {
        if (string.IsNullOrWhiteSpace(seedDirectory))
        {
            return 0;
        }

        // Checked before the delete: a seed without this directory must not wipe the existing one.
        var sourceDirectory = Path.GetFullPath(Path.Combine(seedDirectory, DocumentationDirectory));
        if (!Directory.Exists(sourceDirectory))
        {
            return 0;
        }

        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(rootConfigurationPath))!;
        var targetDirectory = Path.Combine(dataDirectory, DocumentationDirectory);

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        var copiedFileCount = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile);
            copiedFileCount++;
        }

        return copiedFileCount;
    }

    private static bool IsSeedRootFile(string sourceDirectory, string sourceFile) =>
        string.Equals(Path.GetRelativePath(sourceDirectory, sourceFile), SeedRootFileName, StringComparison.OrdinalIgnoreCase);
}
