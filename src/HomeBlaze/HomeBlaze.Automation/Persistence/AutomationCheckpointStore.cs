using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HomeBlaze.Services;

namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// The checkpoint file of one automation, at the canonical path of the automation below the folder
/// <see cref="HomeBlazePaths.AutomationStateDirectory"/> of the data directory, such as
/// <c>AutomationState/Demo/ConveyorGuard.json</c> for <c>/Demo/ConveyorGuard</c>. Each path has a file of its own: a
/// character that a file name cannot hold, the escape character <c>%</c>, a name that Windows reserves and a folder name
/// that ends with <c>.json</c> are percent-encoded. Without a path nothing is read or written. A write replaces the file
/// atomically, so it holds the old or the new checkpoint and never a part of one.
/// </summary>
internal sealed class AutomationCheckpointStore(string? filePath)
{
    private const string Extension = ".json";

    // The Windows set, so that data directories move between platforms without renames, and the escape character.
    private static readonly SearchValues<char> EscapedCharacters = SearchValues.Create("<>:\"\\|?*%");

    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

        var names = automationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new string[names.Length == 0 ? 1 : names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            segments[index] = ToFileName(names[index], isFolder: index < names.Length - 1);
        }

        // The root is named by its path, which no segment can hold.
        segments[^1] = (names.Length == 0 ? "%2F" : segments[^1]) + Extension;
        return Path.Combine([dataDirectory, HomeBlazePaths.AutomationStateDirectory, .. segments]);
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

    /// <summary>
    /// Renames the file to <c>{name}.unreadable-{timestamp}</c> next to it, so that a file that cannot be read is kept
    /// and not replaced by the next write; returns the new path, or null without a path.
    /// </summary>
    /// <exception cref="IOException">The file cannot be renamed.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be renamed.</exception>
    public string? MoveAside(DateTimeOffset now)
    {
        if (FilePath is null)
        {
            return null;
        }

        var target = $"{FilePath}.unreadable-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}";
        lock (_lock)
        {
            File.Move(FilePath, target);
        }

        return target;
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

    /// <summary>
    /// Deletes the file; returns whether there was one. Later writes do nothing, so that a worker that is still stopping
    /// cannot create it again.
    /// </summary>
    /// <exception cref="IOException">The file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be deleted.</exception>
    public bool Delete()
    {
        if (FilePath is null)
        {
            return false;
        }

        lock (_lock)
        {
            _isDeleted = true;
            var existed = File.Exists(FilePath);
            File.Delete(FilePath);
            return existed;
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

    // Percent-encodes what would make two segments share a file name, or a segment name no file: what a file name cannot
    // hold, the escape character, the dot names, a trailing dot or space (which Windows drops), the first letter of a
    // reserved name, and the dot of a folder name that ends with the extension of a file next to it.
    private static string ToFileName(string segment, bool isFolder)
    {
        var builder = new StringBuilder(segment.Length + 8);
        for (var index = 0; index < segment.Length; index++)
        {
            var character = segment[index];
            if (MustEscape(segment, index, isFolder))
            {
                builder.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool MustEscape(string segment, int index, bool isFolder)
    {
        var character = segment[index];
        if (EscapedCharacters.Contains(character) || char.IsControl(character) || segment is "." or "..")
        {
            return true;
        }

        return index == segment.Length - 1 && character is '.' or ' ' || IsPositionEscaped(segment, index, isFolder);
    }

    private static bool IsPositionEscaped(string segment, int index, bool isFolder)
    {
        if (index == 0 && ReservedNames.Contains(segment.Split('.')[0]))
        {
            return true;
        }

        return isFolder && index == segment.Length - Extension.Length && segment.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }
}
