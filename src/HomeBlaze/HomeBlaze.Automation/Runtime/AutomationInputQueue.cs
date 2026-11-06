using System.Threading.Channels;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The bounded input queue of one automation. Writers of changes and events never wait. An input that does not fit, or that
/// continues a feedback chain beyond <see cref="MaxChainDepth"/>, is dropped and counted, and the worker reports the
/// count; external events are never dropped silently. The chain depth flows with the asynchronous flow of a run while the
/// run is active, so the property writes, operations and events of a run cause inputs one deeper than the input of the
/// run. Work that outlives the run, and changes applied later on another flow (for example a device confirming a write
/// from its own thread), start a new chain.
/// </summary>
internal sealed class AutomationInputQueue
{
    public const int Capacity = 1024;
    public const int MaxChainDepth = 16;

    private static readonly AsyncLocal<RunMarker?> CurrentRun = new();

    private readonly Channel<AutomationInput> _channel = Channel.CreateBounded<AutomationInput>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    private int _overloaded;
    private int _feedbackChains;

    /// <summary>
    /// The chain depth of an input caused now: one more than the input of the run on this flow while that run is active,
    /// 0 otherwise, also for work that captured the context of a run that has ended.
    /// </summary>
    public static int CausedChainDepth => CurrentRun.Value is { IsActive: true } run ? run.Depth : 0;

    public int Count => _channel.Reader.Count;

    /// <summary>Marks the flow as running an input of the depth until the scope is disposed.</summary>
    public static RunScope EnterRun(int inputChainDepth)
    {
        var previous = CurrentRun.Value;
        var run = new RunMarker(inputChainDepth + 1);
        CurrentRun.Value = run;
        return new RunScope(run, previous);
    }

    public bool TryEnqueue(AutomationInput input)
    {
        if (input.ChainDepth > MaxChainDepth)
        {
            Interlocked.Increment(ref _feedbackChains);
            return false;
        }

        if (_channel.Writer.TryWrite(input))
        {
            return true;
        }

        Interlocked.Increment(ref _overloaded);
        return false;
    }

    /// <summary>
    /// Enqueues an input that a caller waits for, waiting for room instead of dropping it. Returns false once the queue is
    /// closed.
    /// </summary>
    public async Task<bool> EnqueueWhenRoomAsync(AutomationInput input)
    {
        while (await _channel.Writer.WaitToWriteAsync())
        {
            if (_channel.Writer.TryWrite(input))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Refuses every later input and returns the inputs that were still queued.</summary>
    public List<AutomationInput> Close()
    {
        _channel.Writer.TryComplete();
        var remaining = new List<AutomationInput>();
        while (_channel.Reader.TryRead(out var input))
        {
            remaining.Add(input);
        }

        return remaining;
    }

    public ValueTask<AutomationInput> ReadAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAsync(cancellationToken);

    /// <summary>Gets the inputs dropped since the last call and counts from zero again.</summary>
    public QueueFaults TakeFaults() =>
        new(Interlocked.Exchange(ref _overloaded, 0), Interlocked.Exchange(ref _feedbackChains, 0));

    internal sealed class RunMarker(int depth)
    {
        private volatile bool _isActive = true;

        public int Depth { get; } = depth;

        public bool IsActive => _isActive;

        public void End() => _isActive = false;
    }

    internal readonly struct RunScope : IDisposable
    {
        private readonly RunMarker _run;
        private readonly RunMarker? _previous;

        internal RunScope(RunMarker run, RunMarker? previous)
        {
            _run = run;
            _previous = previous;
        }

        public void Dispose()
        {
            _run.End();
            CurrentRun.Value = _previous;
        }
    }
}
