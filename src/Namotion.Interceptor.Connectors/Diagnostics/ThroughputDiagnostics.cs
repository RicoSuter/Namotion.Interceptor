namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Read-only view over a connector's change throughput, averaged over the last 60 seconds.
/// </summary>
/// <remarks>
/// Direction is stated once, from the subject tree's point of view, and means the same thing for
/// clients and servers: incoming is changes flowing into the subject tree, outgoing is changes
/// flowing out of it. For a client source, incoming is what the external system pushed; for a
/// server, incoming is what a connected client wrote.
/// <para>
/// A <c>null</c> rate means the connector does not measure that direction, which is decided at
/// construction and never changes. It is distinct from a rate of <c>0.0</c>, which means the
/// connector measures the direction and nothing is flowing.
/// </para>
/// </remarks>
public sealed class ThroughputDiagnostics
{
    private readonly ThroughputCounter? _incoming;
    private readonly ThroughputCounter? _outgoing;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThroughputDiagnostics"/> class.
    /// </summary>
    public ThroughputDiagnostics(ThroughputCounter? incoming, ThroughputCounter? outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>
    /// Gets a view that measures neither direction.
    /// </summary>
    public static ThroughputDiagnostics NotInstrumented { get; } = new(null, null);

    /// <summary>
    /// Gets the average changes per second flowing into the subject tree, or <c>null</c> if this
    /// connector does not measure it.
    /// </summary>
    public double? IncomingPerSecond => _incoming?.CurrentRate;

    /// <summary>
    /// Gets the average changes per second flowing out of the subject tree, or <c>null</c> if this
    /// connector does not measure it.
    /// </summary>
    public double? OutgoingPerSecond => _outgoing?.CurrentRate;
}
