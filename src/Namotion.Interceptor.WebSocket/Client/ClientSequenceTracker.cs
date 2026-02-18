using System.Threading;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// Tracks the expected next sequence number from the server and detects gaps.
/// A gap indicates missed updates, requiring the client to reconnect and resync.
/// </summary>
internal sealed class ClientSequenceTracker
{
    private long _expectedNextSequence;

    /// <summary>
    /// Gets the next sequence number expected from the server.
    /// Thread-safe: uses Volatile.Read for cross-thread visibility (e.g., logging from other contexts).
    /// </summary>
    public long ExpectedNextSequence => Volatile.Read(ref _expectedNextSequence);

    /// <summary>
    /// Initializes tracking from the Welcome message's sequence number.
    /// The next expected sequence is one past the Welcome snapshot.
    /// </summary>
    public void InitializeFromWelcome(long welcomeSequence)
    {
        Volatile.Write(ref _expectedNextSequence, welcomeSequence + 1);
    }

    /// <summary>
    /// Validates an incoming Update message's sequence number.
    /// Returns true if valid (no gap), false if a gap is detected.
    /// When valid and sequence is non-zero, advances the tracker.
    /// </summary>
    /// <param name="sequence">The sequence from the Update message.</param>
    public bool IsUpdateValid(long sequence)
    {
        if (sequence != Volatile.Read(ref _expectedNextSequence))
        {
            return false;
        }

        Volatile.Write(ref _expectedNextSequence, sequence + 1);
        return true;
    }

    /// <summary>
    /// Checks if a Heartbeat message's sequence is consistent with client state.
    /// Returns true if in sync, false if the server is ahead (gap detected).
    /// </summary>
    /// <param name="sequence">The server's current sequence from the Heartbeat.</param>
    public bool IsHeartbeatInSync(long sequence)
    {
        // Server sequence should be strictly less than our expected next.
        // If server is at or past expected, we missed updates.
        return sequence < Volatile.Read(ref _expectedNextSequence);
    }
}
