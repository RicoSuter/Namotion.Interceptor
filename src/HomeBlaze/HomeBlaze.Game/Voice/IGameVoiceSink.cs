namespace HomeBlaze.Game.Voice;

/// <summary>
/// One page of a listener with the speaker on.
/// </summary>
internal interface IGameVoiceSink
{
    /// <summary>
    /// Sends a mixed frame to the page. Called on the mixer's tick, so it returns without waiting for the browser. The
    /// sinks of one listener share the frame and never change it.
    /// </summary>
    /// <remarks>
    /// May be called once more after the sink's registration was disposed, by a tick that already took the sinks, and
    /// must not throw then either.
    /// </remarks>
    void Send(byte[] frame);
}
