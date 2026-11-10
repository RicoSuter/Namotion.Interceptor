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
/// <c>AutomationState/Demo/ConveyorGuard.json</c> for <c>/Demo/ConveyorGuard</c>; each path has a file of its own.
/// Without a path nothing is read or written. A write replaces the file atomically, so that also after a power loss it
/// holds the old or the new checkpoint and never a part of one. A store owns its file from its creation until it is
/// released or a later store of the same file is created, process-wide; a store that does not own its file writes, moves
/// and deletes nothing, so that two workers never write one file.
/// </summary>
internal sealed class AutomationCheckpointStore
{
    private const string Extension = ".json";
    private const string TemporaryExtension = ".tmp";
    private const int TemporaryIdLength = 32;

    // The Windows set, so that data directories move between platforms without renames, and the escape character.
    private static readonly SearchValues<char> EscapedCharacters = SearchValues.Create("<>:\"\\|?*%");

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");

    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Taken only to change the owner of a file or to forget a file without one, never while a file is written.
    private static readonly Lock OwnersLock = new();
    private static readonly Dictionary<string, FileOwner> Owners = new(StringComparer.Ordinal);

    private readonly FileOwner? _file;
    private bool _isDeleted;

    /// <summary>Creates the store of the file and makes it the owner of the file; without a path it has no file.</summary>
    public AutomationCheckpointStore(string? filePath)
    {
        FilePath = filePath;
        if (filePath is not null)
        {
            _file = Claim(filePath, this);
        }
    }

    public string? FilePath { get; }

    /// <summary>Gets the path of the checkpoint file of the automation at the canonical path, or null without both.</summary>
    public static string? GetFilePath(string? dataDirectory, string? automationPath)
    {
        if (dataDirectory is null || automationPath is null)
        {
            return null;
        }

        var names = automationPath.Split(SubjectPathResolver.Separator, StringSplitOptions.RemoveEmptyEntries);
        var segments = new string[names.Length == 0 ? 1 : names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            segments[index] = ToFileName(names[index], isFolder: index < names.Length - 1);
        }

        // The root is named by its path, which no segment can hold.
        segments[^1] = (names.Length == 0 ? "%2F" : segments[^1]) + Extension;
        return Path.Combine([dataDirectory, HomeBlazePaths.AutomationStateDirectory, .. segments]);
    }

    /// <summary>
    /// Reads the checkpoint, or null when there is no path or no file. A store that owns its file first deletes the
    /// temporary files that an interrupted write left next to it, as far as it can.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    /// <exception cref="JsonException">The file holds no checkpoint.</exception>
    public AutomationCheckpoint? Read()
    {
        DeleteTemporaryFiles();
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
        if (_file is null)
        {
            return null;
        }

        var target = $"{FilePath}.unreadable-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}";
        lock (_file.Lock)
        {
            if (!ReferenceEquals(_file.Store, this))
            {
                return null;
            }

            File.Move(FilePath!, target);
        }

        return target;
    }

    /// <summary>Replaces the file with the checkpoint; does nothing without a path, without owning it or after <see cref="Delete"/>.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Write(AutomationCheckpoint checkpoint)
    {
        if (_file is null)
        {
            return;
        }

        lock (_file.Lock)
        {
            if (!_isDeleted && ReferenceEquals(_file.Store, this))
            {
                WriteAtomically(FilePath!, JsonSerializer.Serialize(checkpoint, AutomationCheckpointJsonContext.Default.AutomationCheckpoint));
            }
        }
    }

    /// <summary>
    /// Deletes the file of the store that owns it; returns whether there was one. Later writes do nothing, so that a worker
    /// that is still stopping cannot create it again.
    /// </summary>
    /// <exception cref="IOException">The file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be deleted.</exception>
    public bool Delete()
    {
        if (_file is null)
        {
            return false;
        }

        lock (_file.Lock)
        {
            _isDeleted = true;
            if (!ReferenceEquals(_file.Store, this))
            {
                return false;
            }

            var existed = File.Exists(FilePath);
            File.Delete(FilePath!);
            return existed;
        }
    }

    /// <summary>Gives up the file, so that the store writes, moves and deletes nothing anymore.</summary>
    public void Release()
    {
        if (_file is null)
        {
            return;
        }

        lock (OwnersLock)
        {
            lock (_file.Lock)
            {
                if (!ReferenceEquals(_file.Store, this))
                {
                    return;
                }

                _file.Store = null;
            }

            Owners.Remove(FilePath!);
        }
    }

    private static FileOwner Claim(string filePath, AutomationCheckpointStore store)
    {
        lock (OwnersLock)
        {
            if (!Owners.TryGetValue(filePath, out var file))
            {
                file = new FileOwner();
                Owners.Add(filePath, file);
            }

            lock (file.Lock)
            {
                file.Store = store;
            }

            return file;
        }
    }

    // Flushed to the disk before the move, since a file system may otherwise persist the move before the content, which
    // leaves an empty checkpoint after a power loss.
    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

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

    private void DeleteTemporaryFiles()
    {
        if (_file is null || Path.GetDirectoryName(FilePath) is not { } directory || !Directory.Exists(directory))
        {
            return;
        }

        var prefix = Path.GetFileName(FilePath) + ".";
        lock (_file.Lock)
        {
            if (!ReferenceEquals(_file.Store, this))
            {
                return;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, prefix + "*" + TemporaryExtension))
                {
                    if (IsTemporaryFile(Path.GetFileName(path.AsSpan()), prefix))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover that cannot be deleted now is deleted at a later start; the checkpoint is read regardless.
            }
        }
    }

    // Only the name of a temporary file of this checkpoint, not one of another checkpoint whose name starts the same.
    private static bool IsTemporaryFile(ReadOnlySpan<char> name, string prefix)
    {
        if (name.Length != prefix.Length + TemporaryIdLength + TemporaryExtension.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(TemporaryExtension, StringComparison.Ordinal))
        {
            return false;
        }

        return !name.Slice(prefix.Length, TemporaryIdLength).ContainsAnyExcept(HexDigits);
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

    /// <summary>The store that owns a file, and the lock of its writes.</summary>
    private sealed class FileOwner
    {
        public Lock Lock { get; } = new();

        // Changed under the lock.
        public AutomationCheckpointStore? Store { get; set; }
    }
}
