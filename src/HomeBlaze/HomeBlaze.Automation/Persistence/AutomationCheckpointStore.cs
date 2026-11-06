using System.Text.Json;
using HomeBlaze.Services;

namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// The checkpoint file of one automation, at the canonical path of the automation below the folder
/// <see cref="HomeBlazePaths.AutomationsDirectory"/> of the data directory, such as
/// <c>Automations/Demo/ConveyorGuard.json</c> for <c>/Demo/ConveyorGuard</c>. Without a path nothing is read or written.
/// A write replaces the file atomically, so it holds the old or the new checkpoint and never a part of one.
/// </summary>
internal sealed class AutomationCheckpointStore(string? filePath)
{
    // The Windows set, so that data directories move between platforms without renames.
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    private readonly Lock _lock = new();
    private bool _isDeleted;

    public string? FilePath { get; } = filePath;

    /// <summary>Gets the path of the checkpoint file of the automation at the canonical path, or null without both.</summary>
    public static string? GetFilePath(string? dataDirectory, string? automationPath)
    {
        if (dataDirectory is null || automationPath is null)
        {
            return null;
        }

        var segments = automationPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(ToFileName).ToList();
        if (segments.Count == 0)
        {
            segments.Add("_root");
        }

        segments[^1] += ".json";
        return Path.Combine([dataDirectory, HomeBlazePaths.AutomationsDirectory, .. segments]);
    }

    /// <summary>Reads the checkpoint, or null when there is no path or no file.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    /// <exception cref="JsonException">The file holds no checkpoint.</exception>
    public AutomationCheckpoint? Read()
    {
        if (FilePath is null || !File.Exists(FilePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AutomationCheckpointJsonContext.Default.AutomationCheckpoint);
    }

    /// <summary>Replaces the file with the checkpoint; does nothing without a path or after <see cref="Delete"/>.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Write(AutomationCheckpoint checkpoint)
    {
        if (FilePath is null)
        {
            return;
        }

        lock (_lock)
        {
            if (!_isDeleted)
            {
                WriteAtomically(FilePath, JsonSerializer.Serialize(checkpoint, AutomationCheckpointJsonContext.Default.AutomationCheckpoint));
            }
        }
    }

    /// <summary>Deletes the file. Later writes do nothing, so that a worker that is still stopping cannot create it again.</summary>
    /// <exception cref="IOException">The file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be deleted.</exception>
    public void Delete()
    {
        if (FilePath is null)
        {
            return;
        }

        lock (_lock)
        {
            _isDeleted = true;
            File.Delete(FilePath);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ToFileName(string segment)
    {
        if (segment is "." or "..")
        {
            return "_";
        }

        var characters = segment.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (InvalidFileNameCharacters.Contains(characters[index]) || char.IsControl(characters[index]))
            {
                characters[index] = '_';
            }
        }

        return new string(characters);
    }
}
