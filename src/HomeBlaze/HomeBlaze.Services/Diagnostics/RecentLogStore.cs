using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Keeps the most recent log entries at or above a minimum level in memory, for <see cref="HostLogs"/>. Registered by
/// <see cref="ServiceCollectionExtensions.AddHomeBlazeServices"/>, which also registers the logger provider that fills it.
/// </summary>
public sealed class RecentLogStore
{
    internal const int DefaultCapacity = 1000;
    internal const int MaximumCapacity = 10_000;
    internal const LogLevel DefaultMinimumLevel = LogLevel.Warning;
    internal const int MaximumMessageLength = 2048;
    internal const int MaximumExceptionMessageLength = 512;

    // Text is cut to its limit plus this margin before it is redacted, so a secret cut at the limit is still recognized.
    private const int RedactionMargin = 256;

    private const string Ellipsis = "…";

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Serializes reading a configuration and applying it with <see cref="Configure"/> across all subjects that
    /// configure this store, so the store never ends up with values read before a concurrent change.
    /// </summary>
    internal Lock ConfigurationLock { get; } = new();

    // Guarded by _lock: a ring of _count entries starting at the oldest, _start.
    private RecentLogEntry[] _entries;
    private int _start;
    private int _count;
    private LogLevel _minimumLevel;

    // Read without the lock by IsEnabled on every logging call; LogLevel.None while the capacity is 0.
    private volatile int _threshold;

    private long _warningCount;
    private long _errorCount;

    internal RecentLogStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new RecentLogEntry[DefaultCapacity];
        _minimumLevel = DefaultMinimumLevel;
        _threshold = (int)DefaultMinimumLevel;
    }

    /// <summary>The lowest level that is kept.</summary>
    internal LogLevel MinimumLevel
    {
        get
        {
            lock (_lock)
            {
                return _minimumLevel;
            }
        }
    }

    /// <summary>The most entries that are kept; 0 keeps none.</summary>
    internal int Capacity
    {
        get
        {
            lock (_lock)
            {
                return _entries.Length;
            }
        }
    }

    /// <summary>The entries currently kept.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Warning entries kept since the process started, including ones that were replaced since.</summary>
    internal long WarningCount => Interlocked.Read(ref _warningCount);

    /// <summary>Error and Critical entries kept since the process started, including ones that were replaced since.</summary>
    internal long ErrorCount => Interlocked.Read(ref _errorCount);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && (int)level >= _threshold;

    /// <summary>
    /// Sets the minimum level, the default for null, <see cref="LogLevel.None"/> or a value that is no level, and the
    /// capacity, clamped to 0..<see cref="MaximumCapacity"/>. A smaller capacity drops the oldest entries.
    /// </summary>
    internal void Configure(LogLevel? minimumLevel, int capacity)
    {
        var level = minimumLevel is { } value && value != LogLevel.None && Enum.IsDefined(value) ? value : DefaultMinimumLevel;
        capacity = Math.Clamp(capacity, 0, MaximumCapacity);

        lock (_lock)
        {
            if (capacity != _entries.Length)
            {
                Resize(capacity);
            }

            _minimumLevel = level;
            _threshold = capacity == 0 ? (int)LogLevel.None : (int)level;
        }
    }

    /// <summary>Adds an entry, shortening and redacting its text. Entries below the minimum level are ignored.</summary>
    internal void Add(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var entry = new RecentLogEntry(
            _timeProvider.GetUtcNow(),
            level,
            category,
            eventId.Id,
            eventId.Name,
            Shorten(message, MaximumMessageLength),
            exception?.GetType().FullName,
            exception is null ? null : Shorten(GetFirstLine(exception.Message), MaximumExceptionMessageLength));

        lock (_lock)
        {
            // The configuration may have changed since the caller checked IsEnabled; also rejects None.
            if (!IsEnabled(level))
            {
                return;
            }

            var capacity = _entries.Length;
            var index = (_start + _count) % capacity;
            if (_count == capacity)
            {
                _start = (_start + 1) % capacity;
            }
            else
            {
                _count++;
            }

            _entries[index] = entry;
        }

        if (level == LogLevel.Warning)
        {
            Interlocked.Increment(ref _warningCount);
        }
        else if (level >= LogLevel.Error)
        {
            Interlocked.Increment(ref _errorCount);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxCount"/> entries, newest first, that match all given filters: at least
    /// <paramref name="minimumLevel"/>, written at or after <paramref name="since"/>, with a category starting with
    /// <paramref name="category"/> and with <paramref name="contains"/> in the message, exception type or exception
    /// message. Text filters ignore case.
    /// </summary>
    internal List<RecentLogEntry> GetEntries(LogLevel? minimumLevel, DateTimeOffset? since, string? contains, string? category, int maxCount)
    {
        // Copied under the lock and filtered outside it, so a query never holds up a logging thread for long.
        RecentLogEntry[] snapshot;
        lock (_lock)
        {
            snapshot = new RecentLogEntry[_count];
            for (var index = 0; index < _count; index++)
            {
                snapshot[index] = _entries[(_start + index) % _entries.Length];
            }
        }

        var result = new List<RecentLogEntry>(Math.Min(maxCount, snapshot.Length));
        for (var index = snapshot.Length - 1; index >= 0 && result.Count < maxCount; index--)
        {
            var entry = snapshot[index];
            if ((minimumLevel is null || entry.Level >= minimumLevel) &&
                (since is null || entry.Timestamp >= since) &&
                (string.IsNullOrEmpty(category) || entry.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(contains) || Contains(entry, contains)))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static bool Contains(RecentLogEntry entry, string text) =>
        entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        entry.ExceptionType?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
        entry.ExceptionMessage?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Returns the text redacted and cut to at most <paramref name="maximumLength"/> characters.</summary>
    internal static string Shorten(string text, int maximumLength) =>
        Truncate(RecentLogRedaction.Redact(Truncate(text, maximumLength + RedactionMargin)), maximumLength);

    private static string Truncate(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
        {
            return text;
        }

        var length = maximumLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), Ellipsis);
    }

    private static string GetFirstLine(string text)
    {
        var lineEnd = text.AsSpan().IndexOfAny('\r', '\n');
        return lineEnd < 0 ? text : text[..lineEnd];
    }

    // Keeps the newest entries that fit; called under _lock.
    private void Resize(int capacity)
    {
        var kept = Math.Min(_count, capacity);
        var entries = new RecentLogEntry[capacity];
        for (var index = 0; index < kept; index++)
        {
            entries[index] = _entries[(_start + _count - kept + index) % _entries.Length];
        }

        _entries = entries;
        _start = 0;
        _count = kept;
    }
}
