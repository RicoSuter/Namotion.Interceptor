using System.Threading.Channels;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The bounded input queue of one automation. Writers only enqueue and never wait. An input that does not fit, or that
/// continues a feedback chain beyond <see cref="MaxChainDepth"/>, is dropped and counted, and the worker reports the
/// count; external events are never dropped silently. The chain depth flows with the asynchronous flow of a run, so the
/// property writes, operations and events of a run cause inputs one deeper than the input of the run.
/// </summary>
internal sealed class AutomationInputQueue
{
    public const int Capacity = 1024;
    public const int MaxChainDepth = 16;

    private static readonly AsyncLocal<int> CausedDepth = new();

    private readonly Channel<AutomationInput> _channel = Channel.CreateBounded<AutomationInput>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    private int _overloaded;
    private int _feedbackChains;

    /// <summary>The chain depth of an input caused now: one more than the input of the run on this flow, 0 outside a run.</summary>
    public static int CausedChainDepth => CausedDepth.Value;

    public int Count => _channel.Reader.Count;

    /// <summary>Marks the flow as running an input of the depth until the scope is disposed.</summary>
    public static RunScope EnterRun(int inputChainDepth)
    {
        var previous = CausedDepth.Value;
        CausedDepth.Value = inputChainDepth + 1;
        return new RunScope(previous);
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

    public ValueTask<AutomationInput> ReadAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAsync(cancellationToken);

    /// <summary>Gets the inputs dropped since the last call and counts from zero again.</summary>
    public QueueFaults TakeFaults() =>
        new(Interlocked.Exchange(ref _overloaded, 0), Interlocked.Exchange(ref _feedbackChains, 0));

    internal readonly struct RunScope(int previous) : IDisposable
    {
        public void Dispose() => CausedDepth.Value = previous;
    }
}
