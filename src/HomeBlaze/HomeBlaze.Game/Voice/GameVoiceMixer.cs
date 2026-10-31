using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Mixes the voices of one game individually for every listener. Its own lock guards the talker queues, microphone
/// pages and sinks; the game calls it while holding the game's lock, and the mixer never calls into the game.
/// </summary>
internal sealed class GameVoiceMixer
{
    private static readonly object WarnedMarker = new();

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Guarded by _lock.
    private readonly Dictionary<string, Talker> _talkers = new();
    private readonly Dictionary<string, IGameVoiceSink[]> _sinks = new();

    // Sinks whose failure was logged as a warning already; weak, so a closed page's sink is not kept alive.
    private readonly ConditionalWeakTable<IGameVoiceSink, object> _warnedSinks = new();

    // Used only by Mix, which one loop runs at a time.
    private readonly Dictionary<string, short[]> _frames = new();
    private readonly Stack<short[]> _spareFrames = new();
    private readonly List<KeyValuePair<string, IGameVoiceSink[]>> _listeners = [];
    private readonly int[] _sum = new int[GameVoiceSettings.SamplesPerFrame];

    // Used only by Mix: per listener the gain of each talker in the tick it was last mixed, to glide from.
    private readonly Dictionary<string, ListenerGains> _gains = new();
    private long _tick;

    public GameVoiceMixer(TimeProvider timeProvider, ILogger logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sets the only page whose frames count for the player, or none; the player's queued frames are dropped when the
    /// page changes.
    /// </summary>
    public void SetMicrophonePage(string playerId, int? pageId)
    {
        lock (_lock)
        {
            if (pageId is not { } page)
            {
                RemoveTalker(playerId);
            }
            else if (_talkers.TryGetValue(playerId, out var talker))
            {
                if (talker.MicrophonePageId != page)
                {
                    talker.MicrophonePageId = page;
                    talker.Queue.Clear();
                }
            }
            else
            {
                _talkers[playerId] = new Talker(page, _timeProvider);
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
            if (_talkers.TryGetValue(playerId, out var talker) && talker.MicrophonePageId == pageId && talker.RateLimiter.TryTake())
            {
                talker.Queue.Enqueue(frame);
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
    /// Takes one frame from every playing talker and sends every listener with sinks the sum of the frames of the
    /// talkers in its row of the table, scaled by their volume and without its own; sends nothing to a listener that
    /// hears no frame. Exceptions of sinks are logged. Must not run concurrently with itself.
    /// </summary>
    /// <remarks>
    /// A volume that changed since the previous tick glides linearly across the frame from the previous volume to the
    /// new one. A pair new in the row, or one after a tick in which no talker had a frame, glides up from 0. A pair that
    /// left the row glides down to 0 over one more frame of the talker if it has one, and is dropped otherwise.
    /// </remarks>
    public void Mix(GameVolumeTable table)
    {
        try
        {
            _tick++;
            TakeFrames();
            if (_frames.Count == 0)
            {
                return;
            }

            foreach (var (listenerId, sinks) in _listeners)
            {
                MixFor(listenerId, sinks, table);
            }

            DropGainsOfLeftListeners();
        }
        finally
        {
            ReleaseFrames();
        }
    }

    // Clears the scratch state, so sinks unregistered meanwhile are not kept alive until the next tick.
    private void ReleaseFrames()
    {
        foreach (var frame in _frames.Values)
        {
            _spareFrames.Push(frame);
        }

        _frames.Clear();
        _listeners.Clear();
    }

    // Copies the frames out under the lock, so summing and sending run outside it.
    private void TakeFrames()
    {
        lock (_lock)
        {
            foreach (var (playerId, talker) in _talkers)
            {
                TakeFrame(playerId, talker.Queue);
            }

            _listeners.AddRange(_sinks);
        }
    }

    // Callers hold _lock.
    private void TakeFrame(string playerId, TalkerQueue queue)
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
        if (!_gains.TryGetValue(listenerId, out var gains))
        {
            gains = new ListenerGains();
            _gains[listenerId] = gains;
        }

        gains.Tick = _tick;
        if (!TrySum(listenerId, table.GetRow(listenerId), gains.Talkers))
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

    private bool TrySum(string listenerId, ReadOnlySpan<GameVolumeEntry> row, Dictionary<string, TalkerGain> gains)
    {
        Array.Clear(_sum);
        var isAudible = false;
        foreach (var entry in row)
        {
            if (entry.PlayerId == listenerId)
            {
                continue;
            }

            var previous = UpdateGain(gains, entry.PlayerId, entry.Volume);
            if (_frames.TryGetValue(entry.PlayerId, out var frame))
            {
                AddGliding(frame, previous, entry.Volume);
                isAudible = true;
            }
        }

        return FadeOutLeftTalkers(gains) || isAudible;
    }

    // Returns the gain of the previous tick, or 0 when the pair was not mixed in it.
    private float UpdateGain(Dictionary<string, TalkerGain> gains, string talkerId, float volume)
    {
        ref var gain = ref CollectionsMarshal.GetValueRefOrAddDefault(gains, talkerId, out var exists);
        var previous = exists && gain.Tick == _tick - 1 ? gain.Volume : 0;
        gain = new TalkerGain(volume, _tick);
        return previous;
    }

    private bool FadeOutLeftTalkers(Dictionary<string, TalkerGain> gains)
    {
        var isAudible = false;
        foreach (var (talkerId, gain) in gains)
        {
            if (gain.Tick == _tick)
            {
                continue;
            }

            if (gain.Tick == _tick - 1 && _frames.TryGetValue(talkerId, out var frame))
            {
                AddGliding(frame, gain.Volume, 0);
                isAudible = true;
            }

            gains.Remove(talkerId);
        }

        return isAudible;
    }

    private void DropGainsOfLeftListeners()
    {
        foreach (var (listenerId, gains) in _gains)
        {
            if (gains.Tick != _tick)
            {
                _gains.Remove(listenerId);
            }
        }
    }

    // Counts back from the last sample, which gets exactly the new volume, so the next frame continues without a step.
    private void AddGliding(short[] frame, float from, float to)
    {
        var step = (to - from) / _sum.Length;
        for (var index = 0; index < _sum.Length; index++)
        {
            _sum[index] += (int)(frame[index] * (to - step * (_sum.Length - 1 - index)));
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
            var level = _warnedSinks.TryAdd(sink, WarnedMarker) ? LogLevel.Warning : LogLevel.Debug;
            _logger.Log(level, exception, "Sending a voice frame to a page failed");
        }
    }

    // Callers hold _lock. The queue returns its buffer to the pool, which is safe because Mix reads queues only under the lock.
    private void RemoveTalker(string playerId)
    {
        if (_talkers.Remove(playerId, out var talker))
        {
            talker.Queue.Dispose();
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

            var index = IndexOfReference(sinks, sink);
            if (index < 0)
            {
                return;
            }

            if (sinks.Length == 1)
            {
                _sinks.Remove(playerId);
                return;
            }

            // A new array, because a running tick may still iterate the old one outside the lock.
            var remaining = new IGameVoiceSink[sinks.Length - 1];
            sinks.AsSpan(0, index).CopyTo(remaining);
            sinks.AsSpan(index + 1).CopyTo(remaining.AsSpan(index));
            _sinks[playerId] = remaining;
        }
    }

    private static int IndexOfReference(IGameVoiceSink[] sinks, IGameVoiceSink sink)
    {
        for (var index = 0; index < sinks.Length; index++)
        {
            if (ReferenceEquals(sinks[index], sink))
            {
                return index;
            }
        }

        return -1;
    }

    private readonly record struct TalkerGain(float Volume, long Tick);

    private sealed class ListenerGains
    {
        public long Tick { get; set; }

        public Dictionary<string, TalkerGain> Talkers { get; } = new();
    }

    private sealed class Talker(int microphonePageId, TimeProvider timeProvider)
    {
        public int MicrophonePageId { get; set; } = microphonePageId;

        public TalkerQueue Queue { get; } = new(timeProvider);

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
