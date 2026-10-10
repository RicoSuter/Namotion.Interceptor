using System.Collections.Immutable;
using System.Text.Json;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// An agent's ordered timeline, kept in memory and appended to a JSONL file.
/// </summary>
public sealed class AgentTimeline
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _filePath;
    private readonly int _capacity;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Lock _entriesLock = new();
    private ImmutableList<AgentTimelineEntry> _entries = ImmutableList<AgentTimelineEntry>.Empty;

    /// <param name="filePath">The JSONL file, or null to keep the timeline in-process only.</param>
    /// <param name="capacity">How many entries are kept in memory and in the file after loading.</param>
    public AgentTimeline(string? filePath, int capacity = 500)
    {
        _filePath = filePath;
        _capacity = capacity;
    }

    /// <summary>A snapshot of the entries, oldest first.</summary>
    public IReadOnlyList<AgentTimelineEntry> Entries => Volatile.Read(ref _entries);

    /// <summary>
    /// Loads the file, skipping corrupt lines, keeps the newest entries up to the capacity and rewrites the file with them.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<AgentTimelineEntry>();
            foreach (var line in await File.ReadAllLinesAsync(_filePath, cancellationToken))
            {
                if (TryDeserialize(line) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            var kept = entries.Skip(Math.Max(0, entries.Count - _capacity)).ToImmutableList();
            var lines = kept.Select(entry => JsonSerializer.Serialize(entry, SerializerOptions));
            await AtomicFile.WriteAllTextAsync(_filePath, string.Join('\n', lines) + (kept.IsEmpty ? "" : "\n"), cancellationToken);

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
    public async Task AppendAsync(AgentTimelineEntry entry, CancellationToken cancellationToken)
    {
        lock (_entriesLock)
        {
            var entries = _entries.Add(entry);
            _entries = entries.Count > _capacity ? entries.RemoveRange(0, entries.Count - _capacity) : entries;
        }

        if (_filePath is null)
        {
            return;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.AppendAllTextAsync(_filePath, JsonSerializer.Serialize(entry, SerializerOptions) + "\n", cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>The newest non-quiet background runs, oldest first.</summary>
    public IReadOnlyList<AgentTimelineEntry> GetRecentBackgroundResults(int count)
    {
        var results = Entries.Where(entry => entry.Kind == AgentTimelineEntryKind.BackgroundRun && !entry.IsQuiet).ToList();
        return results.Skip(Math.Max(0, results.Count - count)).ToList();
    }

    /// <summary>The time of the newest quiet background run.</summary>
    public DateTimeOffset? GetLastQuietRunTime()
    {
        return Entries.LastOrDefault(entry => entry.Kind == AgentTimelineEntryKind.BackgroundRun && entry.IsQuiet)?.Timestamp;
    }

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
                findings.Insert(0, entry);
            }
        }

        return findings;
    }

    private static AgentTimelineEntry? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentTimelineEntry>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
