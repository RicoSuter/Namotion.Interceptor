using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Writes a <see cref="LogLevel.Debug"/> entry per first-chance exception in the process, at most one
/// per exception type and throw site per minute. Does nothing unless Debug is enabled for its category.
/// Entries are written by a background thread, so the throwing thread never calls into a logging provider.
/// </summary>
internal sealed class FirstChanceExceptionLogger : IHostedLifecycleService
{
    internal const int DefaultMaximumKeyCount = 1024;

    internal const int DefaultQueueCapacity = 256;

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string UnknownThrowSite = "unknown";

    private static readonly object SeenMarker = new();

    private static readonly Action<ILogger, string, string, string, int, string, Exception?> LogException =
        LoggerMessage.Define<string, string, string, int, string>(
            LogLevel.Debug,
            new EventId(1, "FirstChanceException"),
            "First-chance {ExceptionType} thrown at {ThrowSite}: {ExceptionMessage} ({SkippedCount} skipped since the previous entry)" +
            Environment.NewLine + "{CallStack}");

    private static readonly Action<ILogger, int, Exception?> LogKeyLimitReached =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(2, "FirstChanceExceptionKeyLimitReached"),
            "First-chance exception logging reached {MaximumKeyCount} exception types and throw sites; exceptions of other types and throw sites are not logged until restart");

    private static readonly Action<ILogger, int, Exception?> LogEntriesDropped =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(3, "FirstChanceExceptionEntriesDropped"),
            "{DroppedCount} first-chance exception entries were dropped because the log queue was full");

    [ThreadStatic]
    private static bool _isHandling;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumKeyCount;
    private readonly int _queueCapacity;
    private readonly ConcurrentDictionary<(string ExceptionType, string ThrowSite), KeyState> _keys = new();
    private readonly ConditionalWeakTable<Exception, object> _seenExceptions = new();
    private readonly ConcurrentQueue<Entry> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _consumerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _consumerLock = new();
    private int _keyCount;
    private int _keyLimitReported;
    private int _queuedCount;
    private int _droppedCount;
    private volatile bool _isConsumerStarted;
    private volatile bool _isStopped;

    public FirstChanceExceptionLogger(
        ILogger<FirstChanceExceptionLogger> logger,
        TimeProvider? timeProvider = null,
        int maximumKeyCount = DefaultMaximumKeyCount,
        int queueCapacity = DefaultQueueCapacity)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumKeyCount = maximumKeyCount;
        _queueCapacity = queueCapacity;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;

        bool isConsumerStarted;
        lock (_consumerLock)
        {
            _isStopped = true;
            isConsumerStarted = _isConsumerStarted;
        }

        if (!isConsumerStarted)
        {
            return;
        }

        _signal.Release();
        try
        {
            // Queued entries are written while the logging providers are still alive.
            await _consumerCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting; the background consumer thread does not keep the process alive.
        }
    }

    // Not inlined so the frame count Log skips stays exact.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs eventArgs)
    {
        Log(eventArgs.Exception, skippedCallerFrames: 1);
    }

    /// <summary>
    /// Queues an entry for the exception unless Debug is disabled, the thread is already inside this method
    /// or is the consumer thread, the exception instance was seen before, or an entry for the same exception
    /// type and throw site was queued within the last minute. Never throws and never calls a logging provider.
    /// </summary>
    /// <param name="exception">The exception being thrown.</param>
    /// <param name="skippedCallerFrames">The frames between this method and the throwing method, left out of the call stack.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Log(Exception exception, int skippedCallerFrames = 0)
    {
        // Checked before anything else: an exception thrown in here raises this handler again on the same thread.
        if (_isHandling)
        {
            return;
        }

        _isHandling = true;
        try
        {
            if (!_logger.IsEnabled(LogLevel.Debug) || _isStopped)
            {
                return;
            }

            // An exception passing through an await or a rethrow raises the event again with the same instance.
            if (!_seenExceptions.TryAdd(exception, SeenMarker))
            {
                return;
            }

            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var throwSite = GetThrowSite(exception);
            if (!TryBeginEntry(exceptionType, throwSite, out var skippedCount))
            {
                return;
            }

            // At first chance the exception's own stack trace holds only the throwing frame, so the
            // current stack provides the callers; the first frame skipped is this method. It can only
            // be captured here, formatting it is left to the consumer.
            var callStack = new StackTrace(1 + skippedCallerFrames, fNeedFileInfo: false);
            Enqueue(new Entry(exception, callStack, exceptionType, throwSite, skippedCount));
        }
        catch
        {
            // A failure here must not break the code that threw; the guard above stops the loop.
        }
        finally
        {
            _isHandling = false;
        }
    }

    private static string GetThrowSite(Exception exception)
    {
        // At first chance the first line is the throwing frame, with async state machines resolved to their
        // method. Without file information no symbols are loaded on the throwing thread.
        var stackTrace = new StackTrace(exception, fNeedFileInfo: false).ToString();
        if (string.IsNullOrEmpty(stackTrace))
        {
            return UnknownThrowSite;
        }

        var frame = stackTrace.AsSpan();
        var lineEnd = frame.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            frame = frame[..lineEnd];
        }

        frame = frame.TrimStart();
        if (frame.StartsWith("at ", StringComparison.Ordinal))
        {
            frame = frame[3..];
        }

        // Drops anything after the parameters, such as the native offset that Native AOT appends.
        var parametersEnd = frame.LastIndexOf(')');
        if (parametersEnd >= 0)
        {
            frame = frame[..(parametersEnd + 1)];
        }

        return frame.IsEmpty ? UnknownThrowSite : frame.ToString();
    }

    private bool TryBeginEntry(string exceptionType, string throwSite, out int skippedCount)
    {
        skippedCount = 0;
        var key = (exceptionType, throwSite);
        var now = _timeProvider.GetTimestamp();
        if (!_keys.TryGetValue(key, out var state))
        {
            // New keys are dropped once the limit is reached rather than clearing known ones, which
            // would let a key that floods the log (such as a failing exporter) through again.
            if (Interlocked.Increment(ref _keyCount) > _maximumKeyCount)
            {
                Interlocked.Decrement(ref _keyCount);
                if (Interlocked.Exchange(ref _keyLimitReported, 1) == 0)
                {
                    Enqueue(Entry.KeyLimitReached);
                }

                return false;
            }

            if (_keys.TryAdd(key, new KeyState(now)))
            {
                return true;
            }

            Interlocked.Decrement(ref _keyCount);
            state = _keys[key];
        }

        while (true)
        {
            var lastEntryTimestamp = Volatile.Read(ref state.LastEntryTimestamp);
            if (_timeProvider.GetElapsedTime(lastEntryTimestamp, now) < Window)
            {
                Interlocked.Increment(ref state.SkippedCount);
                return false;
            }

            if (Interlocked.CompareExchange(ref state.LastEntryTimestamp, now, lastEntryTimestamp) == lastEntryTimestamp)
            {
                // A skip counted by another thread between the exchange above and this one goes into this
                // entry instead of the next, so the total over all entries stays exact.
                skippedCount = Interlocked.Exchange(ref state.SkippedCount, 0);
                return true;
            }
        }
    }

    private void Enqueue(Entry entry)
    {
        if (Interlocked.Increment(ref _queuedCount) > _queueCapacity)
        {
            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        _queue.Enqueue(entry);
        EnsureConsumerStarted();
        _signal.Release();
    }

    private void EnsureConsumerStarted()
    {
        if (_isConsumerStarted)
        {
            return;
        }

        lock (_consumerLock)
        {
            if (_isConsumerStarted || _isStopped)
            {
                return;
            }

            // UnsafeStart does not flow the throwing thread's execution context, whose current activity
            // would otherwise tag every later entry with that thread's trace.
            new Thread(ConsumeEntries) { IsBackground = true, Name = "First-chance exception logger" }.UnsafeStart();
            _isConsumerStarted = true;
        }
    }

    private void ConsumeEntries()
    {
        // Set for the lifetime of the thread: an exception thrown while writing raises the first-chance
        // event on this thread, and queuing it would feed the consumer its own failures.
        _isHandling = true;
        try
        {
            while (true)
            {
                // One release per queued entry plus one on stop, so an empty queue after a wake means stopped.
                _signal.Wait();
                if (_queue.TryDequeue(out var entry))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    Write(entry);
                }
                else if (_isStopped)
                {
                    return;
                }
            }
        }
        finally
        {
            _consumerCompletion.TrySetResult();
        }
    }

    private void Write(Entry entry)
    {
        try
        {
            if (entry.Exception is null)
            {
                LogKeyLimitReached(_logger, _maximumKeyCount, null);
            }
            else
            {
                LogException(
                    _logger,
                    entry.ExceptionType!,
                    entry.ThrowSite!,
                    entry.Exception.Message,
                    entry.SkippedCount,
                    entry.CallStack!.ToString(),
                    entry.Exception);
            }

            var droppedCount = Interlocked.Exchange(ref _droppedCount, 0);
            if (droppedCount > 0)
            {
                LogEntriesDropped(_logger, droppedCount, null);
            }
        }
        catch
        {
            // A failing logger must not stop the consumer.
        }
    }

    private readonly record struct Entry(
        Exception? Exception,
        StackTrace? CallStack,
        string? ExceptionType,
        string? ThrowSite,
        int SkippedCount)
    {
        /// <summary>Gets the entry reporting that the key limit was reached, the only one without an exception.</summary>
        public static Entry KeyLimitReached => default;
    }

    private sealed class KeyState(long lastEntryTimestamp)
    {
        public long LastEntryTimestamp = lastEntryTimestamp;

        public int SkippedCount;
    }
}
