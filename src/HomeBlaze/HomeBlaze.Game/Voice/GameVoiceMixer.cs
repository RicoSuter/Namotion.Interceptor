using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Mixes the voices of one game individually for every listener. Its own lock guards the speaker queues, microphone
/// pages and sinks; the game calls it while holding the game's lock, and the mixer never calls into the game.
/// </summary>
internal sealed class GameVoiceMixer
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Guarded by _lock.
    private readonly Dictionary<string, Speaker> _speakers = new();
    private readonly Dictionary<string, IGameVoiceSink[]> _sinks = new();

    // Used only by Mix, which one loop runs at a time.
    private readonly Dictionary<string, short[]> _frames = new();
    private readonly Stack<short[]> _spareFrames = new();
    private readonly List<KeyValuePair<string, IGameVoiceSink[]>> _listeners = [];
    private readonly int[] _sum = new int[GameVoiceSettings.SamplesPerFrame];

    public GameVoiceMixer(TimeProvider timeProvider, ILogger logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sets the only page whose frames count for the player, or none, which drops the player's queued frames.
    /// </summary>
    public void SetMicrophonePage(string playerId, int? pageId)
    {
        lock (_lock)
        {
            if (pageId is not { } page)
            {
                RemoveSpeaker(playerId);
            }
            else if (_speakers.TryGetValue(playerId, out var speaker))
            {
                speaker.MicrophonePageId = page;
            }
            else
            {
                _speakers[playerId] = new Speaker(page, _timeProvider);
            }
        }
    }

    /// <summary>
    /// Queues a frame of the player; drops it unless it has exactly <see cref="GameVoiceSettings.FrameBytes"/> bytes,
    /// comes from the player's microphone page and stays within <see cref="GameVoiceSettings.FrameRateTolerance"/>
    /// times the frame rate.
    /// </summary>
    public void Receive(string playerId, int pageId, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != GameVoiceSettings.FrameBytes)
        {
            return;
        }

        lock (_lock)
        {
            if (_speakers.TryGetValue(playerId, out var speaker) && speaker.MicrophonePageId == pageId && speaker.RateLimiter.TryTake())
            {
                speaker.Queue.Enqueue(frame);
            }
        }
    }

    /// <summary>
    /// Adds a page of the player that plays what the player hears; disposing the result removes it again.
    /// </summary>
    public IDisposable RegisterSink(string playerId, IGameVoiceSink sink)
    {
        lock (_lock)
        {
            _sinks[playerId] = _sinks.TryGetValue(playerId, out var sinks) ? [.. sinks, sink] : [sink];
        }

        return new SinkRegistration(this, playerId, sink);
    }

    /// <summary>
    /// Takes one frame from every playing speaker and sends every listener with sinks the sum of the frames of the
    /// speakers in its row of the table, scaled by their volume and without its own; sends nothing to a listener that
    /// hears no frame. Exceptions of sinks are logged.
    /// </summary>
    public void Mix(GameVolumeTable table)
    {
        TakeFrames();
        if (_frames.Count == 0)
        {
            return;
        }

        foreach (var (listenerId, sinks) in _listeners)
        {
            MixFor(listenerId, sinks, table);
        }
    }

    // Copies the frames out under the lock, so summing and sending run outside it.
    private void TakeFrames()
    {
        foreach (var frame in _frames.Values)
        {
            _spareFrames.Push(frame);
        }

        _frames.Clear();
        _listeners.Clear();
        lock (_lock)
        {
            foreach (var (playerId, speaker) in _speakers)
            {
                TakeFrame(playerId, speaker.Queue);
            }

            _listeners.AddRange(_sinks);
        }
    }

    // Callers hold _lock.
    private void TakeFrame(string playerId, SpeakerQueue queue)
    {
        var buffer = _spareFrames.TryPop(out var spare) ? spare : new short[GameVoiceSettings.SamplesPerFrame];
        if (queue.TryDequeue(buffer))
        {
            _frames[playerId] = buffer;
        }
        else
        {
            _spareFrames.Push(buffer);
        }
    }

    private void MixFor(string listenerId, IGameVoiceSink[] sinks, GameVolumeTable table)
    {
        if (!TrySum(listenerId, table.GetRow(listenerId)))
        {
            return;
        }

        // A new array per listener, because the sinks send it asynchronously.
        var frame = GameVoiceCodec.Encode(_sum);
        foreach (var sink in sinks)
        {
            Send(sink, frame);
        }
    }

    private bool TrySum(string listenerId, ReadOnlySpan<GameVolumeEntry> row)
    {
        Array.Clear(_sum);
        var isAudible = false;
        foreach (var entry in row)
        {
            if (entry.PlayerId != listenerId && _frames.TryGetValue(entry.PlayerId, out var frame))
            {
                AddScaled(frame, entry.Volume);
                isAudible = true;
            }
        }

        return isAudible;
    }

    private void AddScaled(short[] frame, float volume)
    {
        for (var index = 0; index < _sum.Length; index++)
        {
            _sum[index] += (int)(frame[index] * volume);
        }
    }

    private void Send(IGameVoiceSink sink, byte[] frame)
    {
        try
        {
            sink.Send(frame);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Sending a voice frame to a page failed");
        }
    }

    // Callers hold _lock. The queue returns its buffer to the pool, which is safe because Mix reads queues only under the lock.
    private void RemoveSpeaker(string playerId)
    {
        if (_speakers.Remove(playerId, out var speaker))
        {
            speaker.Queue.Dispose();
        }
    }

    private void UnregisterSink(string playerId, IGameVoiceSink sink)
    {
        lock (_lock)
        {
            if (!_sinks.TryGetValue(playerId, out var sinks))
            {
                return;
            }

            var remaining = Array.FindAll(sinks, candidate => !ReferenceEquals(candidate, sink));
            if (remaining.Length == 0)
            {
                _sinks.Remove(playerId);
            }
            else
            {
                _sinks[playerId] = remaining;
            }
        }
    }

    private sealed class Speaker(int microphonePageId, TimeProvider timeProvider)
    {
        public int MicrophonePageId { get; set; } = microphonePageId;

        public SpeakerQueue Queue { get; } = new(timeProvider);

        // A burst beyond what the queue keeps would be dropped there anyway.
        public RateLimiter RateLimiter { get; } = new(timeProvider,
            GameVoiceSettings.FrameDuration / GameVoiceSettings.FrameRateTolerance, GameVoiceSettings.ServerQueueMaximum);
    }

    private sealed class SinkRegistration(GameVoiceMixer mixer, string playerId, IGameVoiceSink sink) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                mixer.UnregisterSink(playerId, sink);
            }
        }
    }
}
