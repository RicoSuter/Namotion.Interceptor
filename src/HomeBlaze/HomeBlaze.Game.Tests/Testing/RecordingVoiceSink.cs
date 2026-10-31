using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Records the frames sent to it; thread-safe, because the voice loop of a running game sends from its own thread.
/// </summary>
internal sealed class RecordingVoiceSink : IGameVoiceSink
{
    private readonly Lock _lock = new();
    private readonly List<byte[]> _frames = [];

    public IReadOnlyList<byte[]> Frames
    {
        get
        {
            lock (_lock)
            {
                return [.. _frames];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    public void Send(byte[] frame)
    {
        lock (_lock)
        {
            _frames.Add(frame);
        }
    }
}
