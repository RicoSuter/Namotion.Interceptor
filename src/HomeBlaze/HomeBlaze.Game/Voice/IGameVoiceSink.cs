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
    void Send(byte[] frame);
}
