using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The agent-owned memory: key-value entries whose keys are slash paths, included in the system prompt of every turn.
/// </summary>
public sealed class AgentMemory
{
    /// <summary>The maximum total size of all keys and values in UTF-8 bytes.</summary>
    public const int MaxSize = 8192;

    /// <summary>The maximum number of entries.</summary>
    public const int MaxEntries = 200;

    /// <summary>The maximum length of a normalized key in characters.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>The maximum length of a value in characters.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The key a memory document of the earlier Markdown format is migrated into.</summary>
    public const string LegacyKey = "notes/legacy";

    /// <summary>The version written to the memory file.</summary>
    internal const int FileVersion = 1;

    private const int MaxEchoedKeyLength = 140;

    private static readonly JsonElement UpdateToolSchema = JsonDocument.Parse($$"""
        {
          "type": "object",
          "properties": {
            "changes": {
              "type": "array",
              "description": "The changes, applied in order and all or nothing.",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "key": {
                    "type": "string",
                    "maxLength": {{MaxKeyLength}},
                    "description": "A slash path such as alerts/freezer-temperature. Segments use letters, digits, '-', '_' and '.'. When removing, a key ending in '/' removes that entry and every entry below it."
                  },
                  "value": {
                    "type": "string",
                    "maxLength": {{MaxValueLength}},
                    "description": "The new value. Omit it when removing."
                  },
                  "remove": {
                    "type": "boolean",
                    "description": "true to remove the key instead of setting it."
                  }
                },
                "required": ["key"],
                "additionalProperties": false
              }
            }
          },
          "required": ["changes"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private static readonly JsonWriterOptions FileWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string? _legacyFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile AgentMemorySnapshot _snapshot = AgentMemorySnapshot.Empty;

    // Only read and written while holding _writeLock. A file-backed memory loads before its first write, so a write
    // cannot replace the stored entries with a memory that never saw them.
    private bool _isLoaded;

    /// <param name="filePath">The JSON file, or null to keep memory in-process only.</param>
    /// <param name="timeProvider">The clock for the update time of entries.</param>
    /// <param name="logger">Receives migration and damaged-file messages.</param>
    /// <param name="legacyFilePath">A Markdown memory file of the earlier format, migrated when <paramref name="filePath"/> does not exist yet.</param>
    public AgentMemory(string? filePath, TimeProvider timeProvider, ILogger? logger = null, string? legacyFilePath = null)
    {
        FilePath = filePath;
        _legacyFilePath = legacyFilePath;
        _timeProvider = timeProvider;
        _logger = logger;
        _isLoaded = filePath is null;
    }

    /// <summary>The JSON file, null when the memory is kept in-process only.</summary>
    public string? FilePath { get; }

    /// <summary>Returns the entries of the latest write as one immutable snapshot.</summary>
    public AgentMemorySnapshot GetSnapshot() => _snapshot;

    /// <summary>
    /// Validates a key and normalizes it: trimmed, without leading or trailing slashes.
    /// </summary>
    /// <returns>Why the key is invalid, or null when it is valid.</returns>
    public static string? ValidateKey(string key, out string normalizedKey)
    {
        normalizedKey = key.Trim().Trim('/');
        if (normalizedKey.Length == 0)
        {
            return "The key is empty.";
        }

        if (normalizedKey.Length > MaxKeyLength)
        {
            return $"The key has {normalizedKey.Length} characters; the limit is {MaxKeyLength}.";
        }

        foreach (var segment in normalizedKey.Split('/'))
        {
            if (segment.Length == 0)
            {
                return "The key has an empty segment.";
            }

            if (segment.All(character => character == '.'))
            {
                return "A key segment cannot consist only of dots.";
            }

            if (!segment.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                return "Key segments may only contain letters, digits, '-', '_' and '.'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the memory file, or migrates the legacy Markdown file into <see cref="LegacyKey"/> when only that exists.
    /// A damaged file is logged and kept aside as <c>.damaged</c>; its valid entries are kept, or the memory starts
    /// empty when it cannot be parsed at all.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Applies the changes in order, all or nothing. A change that is invalid, or a result beyond <see cref="MaxEntries"/>
    /// or <see cref="MaxSize"/> that is also larger than the current memory, rejects the whole update. A file-backed
    /// memory that was not loaded yet loads first. A file write failure throws and leaves the memory unchanged.
    /// </summary>
    public async Task<AgentMemoryUpdateResult> UpdateAsync(IReadOnlyList<AgentMemoryChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return AgentMemoryUpdateResult.Failure("Pass at least one change.", _snapshot);
        }

        var normalizedChanges = new (string Key, string? Value, bool IsSubtree)[changes.Count];
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var value = change.Value?.Trim();
            var error = ValidateKey(change.Key, out var key) ?? value switch
            {
                null => null,
                { Length: 0 } => "The value is empty; remove the key instead.",
                { Length: > MaxValueLength } =>
                    $"The value has {value.Length} characters; the limit is {MaxValueLength}. Split it across several keys such as notes/legacy/1.",
                _ => null
            };

            if (error is not null)
            {
                return AgentMemoryUpdateResult.Failure(
                    $"Change {index + 1} (key '{TruncateKey(change.Key)}'): {error} Nothing was applied.", _snapshot);
            }

            normalizedChanges[index] = (key, value, value is null && change.Key.TrimEnd().EndsWith('/'));
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isLoaded)
            {
                await LoadLockedAsync(cancellationToken);
            }

            var current = _snapshot;
            var now = _timeProvider.GetUtcNow();
            var entries = current.EntriesByKey.ToBuilder();
            var applied = ImmutableArray.CreateBuilder<string>(changes.Count);
            foreach (var (key, value, isSubtree) in normalizedChanges)
            {
                if (value is not null)
                {
                    // Removed first so the key takes the casing of this write.
                    entries.Remove(key);
                    entries.Add(key, new AgentMemoryEntry(key, value, now));
                    applied.Add("set " + key);
                }
                else if (isSubtree)
                {
                    var removed = entries.Keys.Where(existing => IsInSubtree(existing, key)).ToList();
                    entries.RemoveRange(removed);
                    applied.Add(removed.Count switch
                    {
                        0 => $"nothing to remove under {key}/",
                        1 => $"removed 1 entry under {key}/",
                        _ => $"removed {removed.Count} entries under {key}/"
                    });
                }
                else
                {
                    applied.Add(entries.Remove(key) ? "removed " + key : "nothing to remove at " + key);
                }
            }

            var updated = new AgentMemorySnapshot(entries.ToImmutable());
            if (updated.Entries.Length > MaxEntries && updated.Entries.Length > current.Entries.Length)
            {
                return AgentMemoryUpdateResult.Failure(
                    $"The memory would hold {updated.Entries.Length} entries; the limit is {MaxEntries} (currently {current.Entries.Length}). " +
                    "Remove entries first. Nothing was applied.", current);
            }

            if (updated.Size > MaxSize && updated.Size > current.Size)
            {
                return AgentMemoryUpdateResult.Failure(
                    $"The memory would hold {updated.Size} bytes; the limit is {MaxSize} (currently {current.Size}). " +
                    "Remove or shorten entries first. Nothing was applied.", current);
            }

            if (FilePath is not null)
            {
                await WriteFileAsync(FilePath, updated, cancellationToken);
            }

            _snapshot = updated;
            return new AgentMemoryUpdateResult(null, applied.ToImmutable(), updated);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Changes only the agent's own memory, so it is neither read-only nor destructive to the environment.
    private static readonly McpToolAnnotations UpdateToolAnnotations =
        new() { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false };

    /// <summary>
    /// Creates the agent-local <c>update_memory</c> tool.
    /// </summary>
    public McpToolInfo CreateUpdateTool() => new()
    {
        Name = "update_memory",
        Description =
            "Change your memory: key-value entries whose keys are slash paths forming a tree, such as alerts/freezer-temperature, " +
            "issues/garage-door or preferences/quiet-hours. Every entry is shown to you with its last update time at the start of " +
            "every run, with the value exactly as stored (JSON-escaped). Each change sets a key to a value (the update time is set " +
            "for you) or removes a key; removing a key that ends in '/' removes its whole subtree. Changes apply in order, all or nothing. " +
            $"Limits: keys {MaxKeyLength} characters, values {MaxValueLength} characters, {MaxEntries} entries and {MaxSize} bytes " +
            "over all keys and values; remove resolved or outdated entries to stay within them.",
        InputSchema = UpdateToolSchema,
        Annotations = UpdateToolAnnotations,
        Handler = async (input, cancellationToken) =>
        {
            var (changes, error) = ParseChanges(input);
            if (error is not null)
            {
                return new { error };
            }

            var result = await UpdateAsync(changes!, cancellationToken);
            return result.Succeeded
                ? new { applied = result.Applied, entries = result.Snapshot.Entries.Length, size = result.Snapshot.Size, maxEntries = MaxEntries, maxSize = MaxSize }
                : (object)new { error = result.Error };
        }
    };

    private static (List<AgentMemoryChange>? Changes, string? Error) ParseChanges(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("changes", out var changesElement) ||
            changesElement.ValueKind != JsonValueKind.Array)
        {
            return (null, "The 'changes' parameter is required: an array of { key, value } or { key, remove: true }.");
        }

        var changes = new List<AgentMemoryChange>();
        foreach (var element in changesElement.EnumerateArray())
        {
            var prefix = $"Change {changes.Count + 1}: ";
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("key", out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                return (null, prefix + "'key' must be a string. Nothing was applied.");
            }

            var hasValue = element.TryGetProperty("value", out var valueElement) && valueElement.ValueKind != JsonValueKind.Null;
            var remove = element.TryGetProperty("remove", out var removeElement) && removeElement.ValueKind == JsonValueKind.True;
            if (hasValue && remove)
            {
                return (null, prefix + "pass either 'value' or \"remove\": true, not both. Nothing was applied.");
            }

            if (remove)
            {
                changes.Add(AgentMemoryChange.Remove(keyElement.GetString()!));
            }
            else if (hasValue && valueElement.ValueKind == JsonValueKind.String)
            {
                changes.Add(AgentMemoryChange.Set(keyElement.GetString()!, valueElement.GetString()!));
            }
            else
            {
                return (null, prefix + "'value' must be a string, or pass \"remove\": true. Nothing was applied.");
            }
        }

        return changes.Count == 0 ? (null, "Pass at least one change.") : (changes, null);
    }

    private static string TruncateKey(string key) => key.Length <= MaxEchoedKeyLength ? key : key[..MaxEchoedKeyLength] + "...";

    private static bool IsInSubtree(string key, string root) =>
        key.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        (key.Length > root.Length && key[root.Length] == '/' && key.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    /// <summary>Caller must hold <see cref="_writeLock"/>.</summary>
    private async Task LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (FilePath is null)
        {
            return;
        }

        if (File.Exists(FilePath))
        {
            _snapshot = await ReadFileAsync(FilePath, cancellationToken);
            if (_legacyFilePath is not null && File.Exists(_legacyFilePath))
            {
                // Left behind by a migration that was interrupted after writing the new file.
                RetireLegacyFile(_legacyFilePath);
            }
        }
        else if (_legacyFilePath is not null && File.Exists(_legacyFilePath))
        {
            _snapshot = await MigrateLegacyFileAsync(FilePath, _legacyFilePath, cancellationToken);
        }
        else
        {
            _snapshot = AgentMemorySnapshot.Empty;
        }

        _isLoaded = true;
    }

    private async Task<AgentMemorySnapshot> ReadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the existence check and this read; treat it as never written.
            return AgentMemorySnapshot.Empty;
        }

        AgentMemoryFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentMemoryFile);
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(exception, "The memory file {Path} is damaged; it is kept as .damaged and the memory starts empty.", filePath);
            KeepDamagedCopy(filePath);
            return AgentMemorySnapshot.Empty;
        }

        if (file?.Version > FileVersion)
        {
            _logger?.LogWarning("The memory file {Path} has the newer version {Version}; it is read as version {FileVersion}.", filePath, file.Version, FileVersion);
        }

        var entries = ImmutableSortedDictionary.CreateBuilder<string, AgentMemoryEntry>(AgentMemoryKeyComparer.Instance);
        var skippedCount = 0;
        foreach (var entry in file?.Entries ?? [])
        {
            // Entries loaded from the file are not held to the value and size limits, so a migrated document stays whole.
            if (entry?.Key is null || entry.Value is null || ValidateKey(entry.Key, out var key) is not null)
            {
                skippedCount++;
                continue;
            }

            entries[key] = key == entry.Key ? entry : entry with { Key = key };
        }

        if (skippedCount > 0)
        {
            _logger?.LogWarning("The memory file {Path} has {Count} invalid entries, which are skipped; the file is kept as .damaged.", filePath, skippedCount);
            KeepDamagedCopy(filePath);
        }

        return new AgentMemorySnapshot(entries.ToImmutable());
    }

    private void KeepDamagedCopy(string filePath)
    {
        try
        {
            File.Copy(filePath, filePath + ".damaged", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "The damaged memory file {Path} could not be kept aside.", filePath);
        }
    }

    private async Task<AgentMemorySnapshot> MigrateLegacyFileAsync(string filePath, string legacyFilePath, CancellationToken cancellationToken)
    {
        var content = (await File.ReadAllTextAsync(legacyFilePath, cancellationToken)).Trim();
        var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(legacyFilePath), TimeSpan.Zero);
        var entries = ImmutableSortedDictionary.Create<string, AgentMemoryEntry>(AgentMemoryKeyComparer.Instance);
        if (content.Length > 0)
        {
            entries = entries.Add(LegacyKey, new AgentMemoryEntry(LegacyKey, content, updated));
        }

        // The new file is written before the old one is moved away, so an interruption loses nothing; the next load
        // then finds the new file and retires the old one.
        var snapshot = new AgentMemorySnapshot(entries);
        await WriteFileAsync(filePath, snapshot, cancellationToken);
        RetireLegacyFile(legacyFilePath);
        _logger?.LogInformation("The memory file {LegacyPath} was migrated to {Path} under the key {Key}.", legacyFilePath, filePath, LegacyKey);
        return snapshot;
    }

    private void RetireLegacyFile(string legacyFilePath)
    {
        try
        {
            File.Move(legacyFilePath, legacyFilePath + ".migrated", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "The migrated memory file {Path} could not be renamed; it is ignored while the new file exists.", legacyFilePath);
        }
    }

    private static Task WriteFileAsync(string filePath, AgentMemorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, FileWriterOptions))
        {
            JsonSerializer.Serialize(writer, new AgentMemoryFile(FileVersion, snapshot.Entries), AgentJsonContext.Default.AgentMemoryFile);
        }

        return AtomicFile.WriteAllTextAsync(filePath, Encoding.UTF8.GetString(buffer.WrittenSpan), cancellationToken);
    }
}

/// <summary>One memory entry. <paramref name="Updated"/> is set by the memory on every write of the key.</summary>
public sealed record AgentMemoryEntry(string Key, string Value, DateTimeOffset Updated);

/// <summary>A change of <see cref="AgentMemory.UpdateAsync"/>: sets <see cref="Value"/> under the key, or removes the key when it is null.</summary>
public sealed record AgentMemoryChange(string Key, string? Value)
{
    /// <summary>Sets the key to the value.</summary>
    public static AgentMemoryChange Set(string key, string value) => new(key, value);

    /// <summary>Removes the key; a key ending in '/' removes its whole subtree.</summary>
    public static AgentMemoryChange Remove(string key) => new(key, null);
}

/// <summary>The outcome of <see cref="AgentMemory.UpdateAsync"/>.</summary>
/// <param name="Error">Why nothing was applied, null on success.</param>
/// <param name="Applied">What each change did, in order; empty on failure.</param>
/// <param name="Snapshot">The memory after the update, or the unchanged memory on failure.</param>
public sealed record AgentMemoryUpdateResult(string? Error, ImmutableArray<string> Applied, AgentMemorySnapshot Snapshot)
{
    /// <summary>Whether the changes were applied.</summary>
    public bool Succeeded => Error is null;

    internal static AgentMemoryUpdateResult Failure(string error, AgentMemorySnapshot snapshot) => new(error, [], snapshot);
}

/// <summary>
/// The memory entries of one write, sorted by key so that a subtree follows its root.
/// </summary>
public sealed class AgentMemorySnapshot
{
    /// <summary>The empty memory.</summary>
    public static readonly AgentMemorySnapshot Empty = new(ImmutableSortedDictionary.Create<string, AgentMemoryEntry>(AgentMemoryKeyComparer.Instance));

    internal AgentMemorySnapshot(ImmutableSortedDictionary<string, AgentMemoryEntry> entriesByKey)
    {
        EntriesByKey = entriesByKey;
        Entries = [.. entriesByKey.Values];
        Size = Entries.Sum(entry => Encoding.UTF8.GetByteCount(entry.Key) + Encoding.UTF8.GetByteCount(entry.Value));
    }

    /// <summary>The entries sorted by key.</summary>
    public ImmutableArray<AgentMemoryEntry> Entries { get; }

    /// <summary>The total size of all keys and values in UTF-8 bytes.</summary>
    public int Size { get; }

    internal ImmutableSortedDictionary<string, AgentMemoryEntry> EntriesByKey { get; }

    /// <summary>Returns the entry with the key, normalized and compared ignoring case, or null.</summary>
    public AgentMemoryEntry? TryGetEntry(string key) =>
        AgentMemory.ValidateKey(key, out var normalizedKey) is null ? EntriesByKey.GetValueOrDefault(normalizedKey) : null;
}

/// <summary>The on-disk format of the memory file.</summary>
internal sealed record AgentMemoryFile(int Version, IReadOnlyList<AgentMemoryEntry>? Entries);

/// <summary>
/// Orders keys ignoring case, with '/' before every other character so a subtree sorts directly after its root.
/// </summary>
internal sealed class AgentMemoryKeyComparer : IComparer<string>
{
    public static readonly AgentMemoryKeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var length = Math.Min(x.Length, y.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = Map(x[index]).CompareTo(Map(y[index]));
            if (difference != 0)
            {
                return difference;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static char Map(char character) => character == '/' ? '\0' : char.ToUpperInvariant(character);
}
