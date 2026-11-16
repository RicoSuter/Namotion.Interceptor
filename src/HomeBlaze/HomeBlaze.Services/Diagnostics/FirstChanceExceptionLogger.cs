using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Writes a <see cref="LogLevel.Debug"/> entry per first-chance exception in the process, at most one
/// per exception type and throw site per minute. Does nothing unless Debug is enabled for its category.
/// </summary>
internal sealed class FirstChanceExceptionLogger : IHostedService
{
    internal const int DefaultMaximumKeyCount = 1024;

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string UnknownThrowSite = "unknown";

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

    [ThreadStatic]
    private static bool _isHandling;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(Type ExceptionType, MethodBase? ThrowSite), KeyState> _keys = new();
    private readonly int _maximumKeyCount;
    private int _keyCount;
    private int _keyLimitReported;

    public FirstChanceExceptionLogger(
        ILogger<FirstChanceExceptionLogger> logger,
        TimeProvider? timeProvider = null,
        int maximumKeyCount = DefaultMaximumKeyCount)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumKeyCount = maximumKeyCount;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        return Task.CompletedTask;
    }

    // Not inlined so the frame count Log skips stays exact.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs eventArgs)
    {
        Log(eventArgs.Exception, skippedCallerFrames: 1);
    }

    /// <summary>
    /// Logs the exception unless Debug is disabled, the thread is already inside this method, or an entry
    /// for the same exception type and throw site was written within the last minute. Never throws.
    /// </summary>
    /// <param name="exception">The exception being thrown.</param>
    /// <param name="skippedCallerFrames">The frames between this method and the throwing method, left out of the call stack.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Log(Exception exception, int skippedCallerFrames = 0)
    {
        // Checked before anything else: an exception thrown while logging raises this handler again on the same thread.
        if (_isHandling)
        {
            return;
        }

        _isHandling = true;
        try
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            var throwSite = exception.TargetSite;
            if (!TryBeginEntry((exception.GetType(), throwSite), out var skippedCount))
            {
                return;
            }

            // At first chance the exception's own stack trace holds only the throwing frame, so the
            // current stack provides the callers; the first frame skipped is this method.
            var callStack = new StackTrace(1 + skippedCallerFrames, fNeedFileInfo: false);

            LogException(
                _logger,
                exception.GetType().FullName ?? exception.GetType().Name,
                throwSite is null ? UnknownThrowSite : $"{throwSite.DeclaringType?.FullName}.{throwSite.Name}",
                exception.Message,
                skippedCount,
                callStack.ToString(),
                exception);
        }
        catch
        {
            // A failing logger must not break the code that threw; the guard above stops the loop.
        }
        finally
        {
            _isHandling = false;
        }
    }

    private bool TryBeginEntry((Type ExceptionType, MethodBase? ThrowSite) key, out int skippedCount)
    {
        skippedCount = 0;
        if (!_keys.TryGetValue(key, out var state))
        {
            // New keys are dropped once the limit is reached rather than clearing known ones, which
            // would let a key that floods the log (such as a failing exporter) through again.
            if (Interlocked.Increment(ref _keyCount) > _maximumKeyCount)
            {
                Interlocked.Decrement(ref _keyCount);
                if (Interlocked.Exchange(ref _keyLimitReported, 1) == 0)
                {
                    LogKeyLimitReached(_logger, _maximumKeyCount, null);
                }

                return false;
            }

            var newState = new KeyState(_timeProvider.GetTimestamp());
            if (_keys.TryAdd(key, newState))
            {
                return true;
            }

            Interlocked.Decrement(ref _keyCount);
            state = _keys[key];
        }

        var now = _timeProvider.GetTimestamp();
        lock (state)
        {
            if (_timeProvider.GetElapsedTime(state.LastEntryTimestamp, now) < Window)
            {
                state.SkippedCount++;
                return false;
            }

            skippedCount = state.SkippedCount;
            state.SkippedCount = 0;
            state.LastEntryTimestamp = now;
            return true;
        }
    }

    private sealed class KeyState(long lastEntryTimestamp)
    {
        public long LastEntryTimestamp = lastEntryTimestamp;

        public int SkippedCount;
    }
}
