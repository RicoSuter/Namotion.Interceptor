using System.Collections.Immutable;
using System.Text.Json;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// An agent's ordered timeline, kept in memory and appended to a JSONL file.
/// </summary>
public sealed class AgentTimeline
{
    private readonly string? _filePath;
    private readonly int _capacity;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Lock _entriesLock = new();
    private ImmutableList<AgentTimelineEntry> _entries = ImmutableList<AgentTimelineEntry>.Empty;

    // Guarded by _fileLock.
    private int _fileLineCount;

    /// <param name="filePath">The JSONL file, or null to keep the timeline in-process only.</param>
    /// <param name="capacity">How many entries are kept in memory and in the file after loading.</param>
    public AgentTimeline(string? filePath, int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _filePath = filePath;
        _capacity = capacity;
    }

    /// <summary>The JSONL file, null when the timeline is kept in-process only.</summary>
    public string? FilePath => _filePath;

    /// <summary>A snapshot of the entries, oldest first.</summary>
    public IReadOnlyList<AgentTimelineEntry> Entries => Volatile.Read(ref _entries);

    /// <summary>
    /// Loads the file, skipping corrupt lines, keeps the newest entries up to the capacity and rewrites the file with them.
    /// A missing file loads as no entries, also when the timeline is loaded again after its file was deleted.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            return;
        }

        // Shared with AppendAsync, so a concurrent append is neither lost nor placed before the entries kept here.
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var kept = ImmutableList<AgentTimelineEntry>.Empty;
            if (File.Exists(_filePath))
            {
                kept = await ReadFileLockedAsync(cancellationToken);
                await WriteFileLockedAsync(kept, cancellationToken);
            }
            else
            {
                _fileLineCount = 0;
            }

            lock (_entriesLock)
            {
                _entries = kept;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Appends an entry in memory (dropping the oldest beyond the capacity) and to the file.
    /// </summary>
    /// <remarks>
    /// If the file write fails, the exception propagates but the entry that was already added to memory is
    /// kept rather than rolled back.
    /// </remarks>
    public async Task AppendAsync(AgentTimelineEntry entry, CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            lock (_entriesLock)
            {
                AddEntryLocked(entry);
            }

            return;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            // Update memory while still holding _fileLock, so across concurrent callers the order entries are
            // added in memory matches the order they are written below, and neither can interleave with a
            // concurrent LoadAsync (which takes the same lock).
            lock (_entriesLock)
            {
                AddEntryLocked(entry);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // The entry is already in memory, so from here on cancellation must not be honored: a cancelled
            // write could leave an unterminated line in the file, which LoadAsync's line-based parsing would
            // then merge with the next append and skip as corrupt, silently losing both entries.
            await File.AppendAllTextAsync(_filePath, Serialize(entry) + "\n", CancellationToken.None);
            _fileLineCount++;

            if (_fileLineCount > _capacity * 2)
            {
                // Keep the file from growing without bound between loads: rewrite it with the entries
                // already kept in memory, which are already capped at the capacity.
                await WriteFileLockedAsync(Entries, cancellationToken);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void AddEntryLocked(AgentTimelineEntry entry)
    {
        var entries = _entries.Add(entry);
        _entries = entries.Count > _capacity ? entries.RemoveRange(0, entries.Count - _capacity) : entries;
    }

    /// <summary>Reads the newest entries of the file up to the capacity. Caller must hold <see cref="_fileLock"/>.</summary>
    private async Task<ImmutableList<AgentTimelineEntry>> ReadFileLockedAsync(CancellationToken cancellationToken)
    {
        var entries = new List<AgentTimelineEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_filePath!, cancellationToken))
        {
            if (TryDeserialize(line) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries.TakeLast(_capacity).ToImmutableList();
    }

    /// <summary>Rewrites the file with exactly the given entries. Caller must hold <see cref="_fileLock"/>.</summary>
    private async Task WriteFileLockedAsync(IReadOnlyCollection<AgentTimelineEntry> entries, CancellationToken cancellationToken)
    {
        var lines = entries.Select(Serialize);
        var content = string.Join('\n', lines) + (entries.Count == 0 ? "" : "\n");
        await AtomicFile.WriteAllTextAsync(_filePath!, content, cancellationToken);
        _fileLineCount = entries.Count;
    }

    /// <summary>
    /// Whether an entry records a background run: a finished run, or the error of a run that failed or was
    /// cancelled, which carries the run's trigger. Chat errors carry no trigger.
    /// </summary>
    public static bool IsBackgroundRun(AgentTimelineEntry entry) =>
        entry.Kind == AgentTimelineEntryKind.BackgroundRun ||
        (entry.Kind == AgentTimelineEntryKind.Error && entry.Trigger is not null);

    /// <summary>The start time of the newest background run, successful or not.</summary>
    public DateTimeOffset? GetLastBackgroundRunTime() => Entries.LastOrDefault(IsBackgroundRun)?.Timestamp;

    /// <summary>The newest non-quiet background runs, oldest first.</summary>
    public IReadOnlyList<AgentTimelineEntry> GetRecentBackgroundResults(int count)
    {
        return Entries.Where(entry => entry.Kind == AgentTimelineEntryKind.BackgroundRun && !entry.IsQuiet).TakeLast(count).ToList();
    }

    /// <summary>The time of the newest quiet background run.</summary>
    public DateTimeOffset? GetLastQuietRunTime() =>
        Entries.LastOrDefault(entry => entry.Kind == AgentTimelineEntryKind.BackgroundRun && entry.IsQuiet)?.Timestamp;

    /// <summary>Non-quiet background runs after the newest assistant reply or divider, oldest first.</summary>
    public IReadOnlyList<AgentTimelineEntry> GetFindingsSinceLastReply()
    {
        var entries = Entries;
        var findings = new List<AgentTimelineEntry>();
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.Kind is AgentTimelineEntryKind.Assistant or AgentTimelineEntryKind.Divider)
            {
                break;
            }

            if (entry.Kind == AgentTimelineEntryKind.BackgroundRun && !entry.IsQuiet)
            {
                findings.Add(entry);
            }
        }

        findings.Reverse();
        return findings;
    }

    private static string Serialize(AgentTimelineEntry entry) =>
        JsonSerializer.Serialize(entry, AgentJsonContext.Default.AgentTimelineEntry);

    private static AgentTimelineEntry? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(line, AgentJsonContext.Default.AgentTimelineEntry);
            if (entry is null)
            {
                return null;
            }

            // The default for ToolCalls is an empty list, but an explicit "toolCalls": null in the file
            // would otherwise override that default with a null that callers do not expect.
            return entry.ToolCalls is null ? entry with { ToolCalls = [] } : entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
