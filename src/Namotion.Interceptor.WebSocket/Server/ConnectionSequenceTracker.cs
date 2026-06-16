using System.Threading;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Tracks the expected next sequence number from a single client connection and detects gaps.
/// Mirror of the client-side <see cref="Client.ClientSequenceTracker"/> for the client-to-server direction.
/// A gap means the server missed one or more client updates and must request a resync.
/// </summary>
internal sealed class ConnectionSequenceTracker
{
    private long _expectedNextSequence = 1; // client's first message after connect is sequence 1

    public long ExpectedNextSequence => Volatile.Read(ref _expectedNextSequence);

    /// <summary>Resets to expect sequence 1 again (new connection / reconnect).</summary>
    public void Reset() => Volatile.Write(ref _expectedNextSequence, 1);

    /// <summary>
    /// Validates an inbound client update sequence. Returns true and advances when it is the
    /// expected next sequence; false when a gap is detected (server missed earlier messages).
    /// </summary>
    public bool IsClientUpdateValid(long sequence)
    {
        if (sequence != Volatile.Read(ref _expectedNextSequence))
        {
            return false;
        }

        Volatile.Write(ref _expectedNextSequence, sequence + 1);
        return true;
    }

    /// <summary>
    /// Realigns the expected next sequence after a gap. The server has accepted (applied) the
    /// out-of-order update at <paramref name="sequence"/>, so the next in-order message is sequence + 1.
    /// Without this, every message after a gap re-triggers a gap and a resync storm.
    /// </summary>
    public void ResyncTo(long sequence) => Volatile.Write(ref _expectedNextSequence, sequence + 1);

    /// <summary>
    /// Idle check: given the client's reported last-sent sequence, returns true when the server has
    /// already received everything the client sent (mirror of the client's heartbeat sequence check).
    /// </summary>
    public bool HasReceivedThrough(long clientLastSentSequence)
        => clientLastSentSequence < Volatile.Read(ref _expectedNextSequence);
}
