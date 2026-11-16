using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Writes a <see cref="LogLevel.Debug"/> entry per first-chance exception in the process, at most one
/// per exception type and throw site per minute, where the throw site is the first method shown on the stack
/// outside the framework. Does nothing unless Debug is enabled for its category.
/// Entries are written by a background task, so the throwing thread never writes to a logging provider.
/// An instance runs once: it is not started again after it was stopped or disposed.
/// </summary>
internal sealed class FirstChanceExceptionLogger : IHostedLifecycleService, IDisposable
{
    internal const int DefaultMaximumKeyCount = 1024;

    internal const int DefaultQueueCapacity = 256;

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string UnknownFrame = "unknown";

    private static readonly string LoggerTypeName = typeof(FirstChanceExceptionLogger).FullName!;

    private static readonly object SeenMarker = new();

    private static readonly StackFrame SentinelFrame = CaptureSentinelFrame();

    private static readonly Action<ILogger, string, string, string, string, int, string, Exception?> LogException =
        LoggerMessage.Define<string, string, string, string, int, string>(
            LogLevel.Debug,
            new EventId(1, "FirstChanceException"),
            "First-chance {ExceptionType} thrown at {ThrowSite} by {ThrowingFrame}: {ExceptionMessage} ({SkippedCount} skipped since the previous entry)" +
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
    private readonly ConcurrentDictionary<(string ExceptionType, string ThrowSite), KeyState> _keys = new();
    private readonly ConditionalWeakTable<Exception, object> _seenExceptions = new();
    private readonly Channel<Entry> _channel;
    private Task? _consumer;
    private int _keyCount;
    private int _keyLimitReported;
    private int _droppedCount;

    public FirstChanceExceptionLogger(
        ILogger<FirstChanceExceptionLogger> logger,
        TimeProvider? timeProvider = null,
        int maximumKeyCount = DefaultMaximumKeyCount,
        int queueCapacity = DefaultQueueCapacity)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumKeyCount = maximumKeyCount;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(queueCapacity)
        {
            // TryWrite returns false when full, which drops the entry instead of blocking the throwing thread.
            FullMode = BoundedChannelFullMode.Wait,
            // Otherwise TryWrite could run the consumer, and with it the logging providers, on the throwing thread.
            AllowSynchronousContinuations = false,
            SingleReader = true
        });
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        StartConsumer();
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
        _channel.Writer.TryComplete();
        if (_consumer is null)
        {
            return;
        }

        // Queued entries are written while the logging providers are still alive. When the host gives up
        // waiting the remaining entries are not written; WhenAny instead of WaitAsync, whose
        // OperationCanceledException would itself be a first-chance exception.
        var hostGaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), hostGaveUp))
        {
            await Task.WhenAny(_consumer, hostGaveUp.Task).ConfigureAwait(false);
        }
    }

    /// <summary>Unsubscribes from first-chance exceptions and stops accepting entries, without waiting for queued ones.</summary>
    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        _channel.Writer.TryComplete();
    }

    /// <summary>Starts the task that writes queued entries.</summary>
    internal void StartConsumer()
    {
        // The consumer must not inherit the caller's execution context, whose current activity would tag
        // every entry with that trace. SuppressFlow throws when flow is already suppressed.
        var flowControl = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
        try
        {
            _consumer = Task.Run(ConsumeEntriesAsync);
        }
        finally
        {
            flowControl?.Undo();
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
    /// or writing an entry, the exception instance was seen before, or an entry for the same exception type
    /// and throw site was queued within the last minute. The throw site is the first frame shown in the calling
    /// stack outside the framework, so frames hidden from stack traces are skipped. When the stack has only framework frames it is the first visible frame of the stack
    /// after the runtime's exception dispatch frames, then the exception's throwing frame. Never throws and
    /// never writes to a logging provider, only <see cref="ILogger.IsEnabled"/> is called on the calling thread.
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
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            // An exception passing through an await or a rethrow raises the event again with the same instance.
            if (!_seenExceptions.TryAdd(exception, SeenMarker))
            {
                return;
            }

            // At first chance the exception's own stack trace holds only the throwing frame, so the
            // current stack provides the callers; the first frame skipped is this method. It can only
            // be captured here, formatting it is left to the consumer.
            var callStack = new StackTrace(1 + skippedCallerFrames, fNeedFileInfo: false);
            var dispatchFrameCount = CountLeadingDispatchFrames(callStack);
            string? throwingFrame = null;
            var throwSite =
                FindCallerOutsideFramework(callStack, dispatchFrameCount) ??
                FindFirstVisibleFrame(callStack, dispatchFrameCount) ??
                (throwingFrame = GetThrowingFrame(exception));

            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var state = TryBeginEntry(exceptionType, throwSite, out var skippedCount);
            if (state is null)
            {
                return;
            }

            throwingFrame ??= GetThrowingFrame(exception);
            if (!TryEnqueue(new Entry(exception, callStack, dispatchFrameCount, exceptionType, throwSite, throwingFrame, skippedCount)))
            {
                // The dropped entry and the skips it carried are reported by the next entry of its key.
                Interlocked.Add(ref state.SkippedCount, skippedCount + 1);
            }
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

    /// <summary>
    /// Returns whether the type is one of the CoreCLR managed exception dispatch types, whose frames
    /// start the call stack at first chance and are shown in formatted stacks on some platforms. Native AOT
    /// uses other type names; there its leading frames are only skipped when hidden from stack traces.
    /// </summary>
    private static bool IsExceptionDispatchType(string? declaringTypeName) =>
        declaringTypeName is "System.Runtime.EH" or "System.Runtime.ExceptionServices.InternalCalls";

    private static int CountLeadingDispatchFrames(StackTrace callStack)
    {
        var index = 0;
        while (index < callStack.FrameCount &&
               callStack.GetFrame(index) is { } frame &&
               IsExceptionDispatchType(DiagnosticMethodInfo.Create(frame)?.DeclaringTypeName))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Returns the first frame from <paramref name="startIndex"/> outside the framework and this class, or null
    /// when there is none. Framework exceptions are mostly thrown by shared helpers, which would put every
    /// cancellation of the process, for example, under one throw site.
    /// </summary>
    private static string? FindCallerOutsideFramework(StackTrace callStack, int startIndex)
    {
        for (var index = startIndex; index < callStack.FrameCount; index++)
        {
            var frame = callStack.GetFrame(index);
            if (frame is null)
            {
                continue;
            }

            // Unlike StackFrame.GetMethod, safe under trimming and Native AOT; null when the method is unknown.
            var declaringTypeName = DiagnosticMethodInfo.Create(frame)?.DeclaringTypeName;
            if (declaringTypeName is null ||
                declaringTypeName.StartsWith("System.", StringComparison.Ordinal) ||
                declaringTypeName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                declaringTypeName.StartsWith(LoggerTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            var formattedFrame = FormatFrameIfShown(frame);
            if (formattedFrame is not null)
            {
                return formattedFrame;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first frame from <paramref name="startIndex"/> that formatted stacks show, or null when there is
    /// none. Frames hidden from stack traces, such as the awaiter helpers, are skipped.
    /// </summary>
    private static string? FindFirstVisibleFrame(StackTrace callStack, int startIndex)
    {
        for (var index = startIndex; index < callStack.FrameCount; index++)
        {
            var frame = callStack.GetFrame(index);
            if (frame is null)
            {
                continue;
            }

            var formattedFrame = FormatFrameIfShown(frame);
            if (formattedFrame is not null)
            {
                return formattedFrame;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the frame formatted like a line of Exception.StackTrace, or null when stack traces hide it or its
    /// method is unknown.
    /// </summary>
    private static string? FormatFrameIfShown(StackFrame frame)
    {
        // The last frame of a stack trace is always shown, even when hidden, so the frame is formatted followed by
        // the sentinel: a single line means the frame was left out.
        var formatted = new StackTrace(new[] { frame, SentinelFrame }).ToString().AsSpan().Trim();
        var lineEnd = formatted.IndexOfAny('\r', '\n');
        return lineEnd < 0 ? null : FormatFirstFrame(formatted[..lineEnd]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StackFrame CaptureSentinelFrame() => new(0, needFileInfo: false);

    private static string GetThrowingFrame(Exception exception)
    {
        // At first chance the exception's stack trace holds only the throwing frame. Without file
        // information no symbols are loaded on the throwing thread. Native AOT exceptions carry no frames at first chance, so this is "unknown" there.
        return FormatFirstFrame(new StackTrace(exception, fNeedFileInfo: false).ToString()) ?? UnknownFrame;
    }

    /// <summary>Returns the method of the first line of a formatted stack trace, as the runtime formats it. Async state machine names are not resolved to their method on Native AOT, where the throw site key stays correct but the fields degrade.</summary>
    private static string? FormatFirstFrame(ReadOnlySpan<char> stackTrace)
    {
        var frame = stackTrace.TrimStart();
        var lineEnd = frame.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            frame = frame[..lineEnd];
        }

        // Drops the leading "at", whatever the language of the runtime's resources.
        frame = frame.TrimStart();
        var wordEnd = frame.IndexOf(' ');
        if (wordEnd >= 0)
        {
            frame = frame[(wordEnd + 1)..];
        }

        // Drops anything after the parameters, such as the native offset that Native AOT appends.
        var parametersEnd = frame.LastIndexOf(')');
        if (parametersEnd >= 0)
        {
            frame = frame[..(parametersEnd + 1)];
        }

        return frame.IsEmpty ? null : frame.ToString();
    }

    /// <summary>Returns the state of the key when an entry is due, or null when the exception is skipped.</summary>
    private KeyState? TryBeginEntry(string exceptionType, string throwSite, out int skippedCount)
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
                if (Interlocked.Exchange(ref _keyLimitReported, 1) == 0 && !TryEnqueue(Entry.KeyLimitReached))
                {
                    Volatile.Write(ref _keyLimitReported, 0);
                }

                return null;
            }

            var newState = new KeyState(now);
            if (_keys.TryAdd(key, newState))
            {
                return newState;
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
                return null;
            }

            if (Interlocked.CompareExchange(ref state.LastEntryTimestamp, now, lastEntryTimestamp) == lastEntryTimestamp)
            {
                // A skip counted by another thread between the exchange above and this one goes into this
                // entry instead of the next, so the total over all entries stays exact.
                skippedCount = Interlocked.Exchange(ref state.SkippedCount, 0);
                return state;
            }
        }
    }

    private bool TryEnqueue(Entry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    private async Task ConsumeEntriesAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var entry))
            {
                Write(entry);
            }
        }
    }

    private void Write(Entry entry)
    {
        // Set per write because continuations run on any pool thread: an exception thrown while writing
        // raises the first-chance event on this thread, and queuing it would feed the consumer its own failures.
        _isHandling = true;
        try
        {
            if (entry.IsKeyLimitReached)
            {
                LogKeyLimitReached(_logger, _maximumKeyCount, null);
            }
            else
            {
                LogException(
                    _logger,
                    entry.ExceptionType,
                    entry.ThrowSite,
                    entry.ThrowingFrame,
                    entry.Exception.Message,
                    entry.SkippedCount,
                    FormatCallStack(entry.CallStack, entry.DispatchFrameCount),
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
        finally
        {
            _isHandling = false;
        }
    }

    private static string FormatCallStack(StackTrace callStack, int dispatchFrameCount)
    {
        return dispatchFrameCount == 0
            ? callStack.ToString()
            : new StackTrace(callStack.GetFrames()[dispatchFrameCount..]).ToString();
    }

    private readonly struct Entry
    {
        public Entry(Exception exception, StackTrace callStack, int dispatchFrameCount, string exceptionType, string throwSite, string throwingFrame, int skippedCount)
        {
            Exception = exception;
            CallStack = callStack;
            DispatchFrameCount = dispatchFrameCount;
            ExceptionType = exceptionType;
            ThrowSite = throwSite;
            ThrowingFrame = throwingFrame;
            SkippedCount = skippedCount;
        }

        private Entry(bool isKeyLimitReached)
        {
            IsKeyLimitReached = isKeyLimitReached;
        }

        public static Entry KeyLimitReached => new(isKeyLimitReached: true);

        /// <summary>Gets a value indicating whether the entry reports that the key limit was reached rather than an exception.</summary>
        [MemberNotNullWhen(false, nameof(Exception), nameof(CallStack), nameof(ExceptionType), nameof(ThrowSite), nameof(ThrowingFrame))]
        public bool IsKeyLimitReached { get; }

        public Exception? Exception { get; }

        public StackTrace? CallStack { get; }

        /// <summary>Gets the number of runtime exception dispatch frames at the start of <see cref="CallStack"/>, left out when it is written.</summary>
        public int DispatchFrameCount { get; }

        public string? ExceptionType { get; }

        public string? ThrowSite { get; }

        public string? ThrowingFrame { get; }

        public int SkippedCount { get; }
    }

    private sealed class KeyState(long lastEntryTimestamp)
    {
        public long LastEntryTimestamp = lastEntryTimestamp;

        public int SkippedCount;
    }
}
