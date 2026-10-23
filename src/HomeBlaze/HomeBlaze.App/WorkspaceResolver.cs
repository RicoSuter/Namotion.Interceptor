using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeBlaze.App;

/// <summary>Where a resolved connection reads and writes.</summary>
public sealed record ResolvedWorkspace
{
    /// <summary>Full path of the root configuration file. Its folder is the data directory.</summary>
    public required string RootConfigurationFile { get; init; }

    /// <summary>Folder that receives the history database, certificates and agent state.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Seed folder, or an empty string to switch seeding and the documentation replacement off.</summary>
    public required string SeedDirectory { get; init; }
}

/// <summary>Maps a chosen folder to the paths the host needs.</summary>
public sealed class WorkspaceResolver
{
    private const string RootConfigurationFileName = "Root.json";

    private readonly string _workspacesRoot;
    private readonly string? _seedDirectory;

    /// <param name="workspacesRoot">Folder that holds one private workspace per mounted folder.</param>
    /// <param name="seedDirectory">Seed folder shipped with the application, or null when none exists.</param>
    public WorkspaceResolver(string workspacesRoot, string? seedDirectory)
    {
        _workspacesRoot = workspacesRoot;
        _seedDirectory = seedDirectory;
    }

    /// <summary>Default location: <c>&lt;LocalApplicationData&gt;/HomeBlaze/Workspaces</c>.</summary>
    public static string DefaultWorkspacesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeBlaze",
        "Workspaces");

    /// <summary>
    /// Uses the folder itself as the data directory. An absent root configuration file is seeded on start,
    /// which is why the folder then has to be empty.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// The folder holds no root configuration file and is not empty.
    /// </exception>
    public ResolvedWorkspace ResolveFullDataFolder(string folderPath)
    {
        var dataDirectory = NormalizeExistingDirectory(folderPath);
        var rootConfigurationFile = Path.Combine(dataDirectory, RootConfigurationFileName);

        // Seeding is gated on the root file being absent, not on the folder being empty, so a folder that
        // already holds unrelated files would receive the whole seed and have its Files/Docs deleted and
        // replaced on every start. Refused rather than confirmed, because that loss is silent and has no undo.
        if (!File.Exists(rootConfigurationFile) && Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            throw new InvalidOperationException(
                $"'{dataDirectory}' is not a HomeBlaze data folder and is not empty. Pick an empty folder to " +
                "set up a new instance, or open this one as a folder of files instead.");
        }

        return new ResolvedWorkspace
        {
            RootConfigurationFile = rootConfigurationFile,
            DataDirectory = dataDirectory,
            SeedDirectory = _seedDirectory ?? string.Empty
        };
    }

    /// <summary>
    /// Mounts the folder as the storage of a private workspace, so that the folder receives nothing but
    /// what the user edits. Seeding is switched off for the same reason.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public ResolvedWorkspace ResolveMarkdownFolder(string folderPath)
    {
        var storageDirectory = NormalizeExistingDirectory(folderPath);
        var dataDirectory = Path.Combine(_workspacesRoot, GetWorkspaceId(storageDirectory));
        Directory.CreateDirectory(dataDirectory);

        var rootConfigurationFile = Path.Combine(dataDirectory, RootConfigurationFileName);
        WriteGeneratedRootConfiguration(rootConfigurationFile, storageDirectory);

        return new ResolvedWorkspace
        {
            RootConfigurationFile = rootConfigurationFile,
            DataDirectory = dataDirectory,
            SeedDirectory = string.Empty
        };
    }

    /// <summary>
    /// Derives a stable workspace folder name from a path, so that reopening the same folder reuses its
    /// history database and certificates.
    /// </summary>
    public static string GetWorkspaceId(string folderPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));

        // Windows and macOS default to case-insensitive filesystems, so two spellings of one path must map to
        // one workspace there. Linux paths are case sensitive and must not be folded together.
        var hashInput = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? normalized.ToLowerInvariant()
            : normalized;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var shortHash = Convert.ToHexStringLower(hash.AsSpan(0, 4));

        var leafName = Path.GetFileName(normalized);
        var sanitized = string.Concat(leafName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (sanitized.Length == 0)
        {
            sanitized = "folder";
        }

        return $"{sanitized}-{shortHash}";
    }

    private static string NormalizeExistingDirectory(string folderPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The folder '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static void WriteGeneratedRootConfiguration(string rootConfigurationFile, string storageDirectory)
    {
        // Rewritten on every resolve rather than only when absent, so that moving the mounted folder and
        // reselecting it updates the connection string instead of silently pointing at the old location.
        var configuration = new JsonObject
        {
            ["$type"] = "HomeBlaze.Storage.FluentStorageContainer",
            ["storageType"] = "disk",
            ["connectionString"] = storageDirectory,
            ["enableFileWatching"] = true
        };

        File.WriteAllText(rootConfigurationFile, configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
