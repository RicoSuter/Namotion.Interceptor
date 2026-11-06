using System.Threading.Channels;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The bounded input queue of one automation. Writers never wait. An input that does not fit, or that continues a
/// feedback chain beyond <see cref="MaxChainDepth"/>, is dropped and counted, and the worker reports the count and makes
/// up for what it can in queue order; external events are never dropped silently. The chain depth flows with the asynchronous flow of a run while the
/// run is active, so the property writes, operations and events of a run cause inputs one deeper than the input of the
/// run. Work that outlives the run, and changes applied later on another flow (for example a device confirming a write
/// from its own thread), start a new chain.
/// </summary>
internal sealed class AutomationInputQueue
{
    public const int Capacity = 1024;
    public const int MaxChainDepth = 16;

    private static readonly AsyncLocal<RunMarker?> CurrentRun = new();

    // Not created for a single reader: that unbounded channel cannot count its items, which IsEmpty needs.
    private readonly Channel<AutomationInput> _channel = Channel.CreateUnbounded<AutomationInput>();

    private readonly Lock _dropLock = new();
    private DroppedInputs? _openBatch;
    private int _count;
    private int _overloaded;
    private int _feedbackChains;

    /// <summary>
    /// The chain depth of an input caused now: one more than the input of the run on this flow while that run is active,
    /// 0 otherwise, also for work that captured the context of a run that has ended.
    /// </summary>
    public static int CausedChainDepth => CurrentRun.Value is { IsActive: true } run ? run.Depth : 0;

    /// <summary>The queued inputs that count against <see cref="Capacity"/>: all but flushes and resynchronizations.</summary>
    public int Count => Volatile.Read(ref _count);

    public bool IsEmpty => _channel.Reader.Count == 0;

    /// <summary>Marks the flow as running an input of the depth until the scope is disposed.</summary>
    public static RunScope EnterRun(int inputChainDepth)
    {
        var previous = CurrentRun.Value;
        var run = new RunMarker(inputChainDepth + 1);
        CurrentRun.Value = run;
        return new RunScope(run, previous);
    }

    /// <summary>
    /// Enqueues the input, or drops and counts it. A drop also queues a <see cref="ResynchronizeInput"/> behind every
    /// input accepted so far, unless the last one queued has no input behind it yet; that one then takes the drop.
    /// </summary>
    public bool TryEnqueue(AutomationInput input)
    {
        if (input.ChainDepth > MaxChainDepth)
        {
            Drop(input, ref _feedbackChains);
            return false;
        }

        if (Interlocked.Increment(ref _count) > Capacity)
        {
            Interlocked.Decrement(ref _count);
            Drop(input, ref _overloaded);
            return false;
        }

        // An input behind the open resynchronization makes the next drop queue a new one behind it, so that the drop is
        // made up for after this input.
        if (Volatile.Read(ref _openBatch) is not null)
        {
            Volatile.Write(ref _openBatch, null);
        }

        if (_channel.Writer.TryWrite(input))
        {
            return true;
        }

        Interlocked.Decrement(ref _count);
        return false;
    }

    /// <summary>Enqueues a flush beyond the capacity, since its caller waits for it; false once the queue is closed.</summary>
    public bool EnqueueFlush(FlushInput flush) => _channel.Writer.TryWrite(flush);

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

    public async ValueTask<AutomationInput> ReadAsync(CancellationToken cancellationToken)
    {
        var input = await _channel.Reader.ReadAsync(cancellationToken);
        if (input is not (FlushInput or ResynchronizeInput))
        {
            Interlocked.Decrement(ref _count);
        }

        return input;
    }

    /// <summary>Gets the inputs dropped since the last call and counts from zero again.</summary>
    public QueueFaults TakeFaults() =>
        new(Interlocked.Exchange(ref _overloaded, 0), Interlocked.Exchange(ref _feedbackChains, 0));

    /// <summary>
    /// Gets the inputs that the resynchronization took, with the drops counted since the last call; later drops queue a
    /// new one.
    /// </summary>
    public DroppedInputs TakeDroppedInputs(ResynchronizeInput resynchronize)
    {
        lock (_dropLock)
        {
            if (ReferenceEquals(_openBatch, resynchronize.Dropped))
            {
                _openBatch = null;
            }
        }

        resynchronize.Dropped.Faults = TakeFaults();
        return resynchronize.Dropped;
    }

    private void Drop(AutomationInput input, ref int counter)
    {
        lock (_dropLock)
        {
            if (_openBatch is not { } batch)
            {
                batch = new DroppedInputs();
                if (_channel.Writer.TryWrite(new ResynchronizeInput(batch)))
                {
                    _openBatch = batch;
                }
            }

            batch.Add(input);
        }

        Interlocked.Increment(ref counter);
    }

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
