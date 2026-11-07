using System.Buffers;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// The frames of one talker waiting for the mixer, in a pooled ring buffer. It plays once it holds its start size,
/// grows the start size by <see cref="GameVoiceSettings.BufferGrowth"/> when it runs dry while playing, and shrinks it
/// by one frame after <see cref="GameVoiceSettings.BufferShrinkDelay"/> of playing without running dry. At the end of
/// every <see cref="GameVoiceSettings.TrimWindow"/> of playing it drops the oldest frames down to its start size when the
/// fewest frames any dequeue found exceeded the start size by more than <see cref="GameVoiceSettings.TrimHeadroom"/>.
/// It never holds more than <see cref="GameVoiceSettings.ServerQueueMaximum"/>, dropping the oldest frame on arrival.
/// Not thread-safe: the mixer guards it with its lock.
/// </summary>
internal sealed class TalkerQueue : IDisposable
{
    private const int Samples = GameVoiceSettings.SamplesPerFrame;
    private const int Capacity = GameVoiceSettings.ServerQueueMaximum;

    private readonly TimeProvider _timeProvider;
    private short[]? _buffer = ArrayPool<short>.Shared.Rent(Capacity * Samples);
    private int _head;
    private long _lastDryTimestamp;
    private long _windowStartTimestamp;
    private int _windowMinimum = int.MaxValue;

    public TalkerQueue(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lastDryTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// The frames waiting.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The frames the queue needs before it plays again.
    /// </summary>
    public int StartFrames { get; private set; } = GameVoiceSettings.ServerQueueStart;

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// The times the queue dropped its oldest frames, at the end of a window or for a frame arriving at the maximum.
    /// </summary>
    public long Trims { get; private set; }

    /// <summary>
    /// The times the queue ran dry while playing.
    /// </summary>
    public long Underruns { get; private set; }

    /// <summary>
    /// Adds a frame of exactly <see cref="GameVoiceSettings.FrameBytes"/> bytes.
    /// </summary>
    public void Enqueue(ReadOnlySpan<byte> frame)
    {
        if (_buffer is not { } buffer)
        {
            throw new ObjectDisposedException(nameof(TalkerQueue));
        }

        if (Count == Capacity)
        {
            Trims++;
            DropOldest();
        }

        var slot = (_head + Count) % Capacity;
        GameVoiceCodec.Decode(frame, buffer.AsSpan(slot * Samples, Samples));
        Count++;
        if (!IsPlaying && Count >= StartFrames)
        {
            IsPlaying = true;
            // The shrink delay and the trim window count only playing time, so a pause in speech affects neither.
            var now = _timeProvider.GetTimestamp();
            _lastDryTimestamp = now;
            StartWindow(now);
        }
    }

    /// <summary>
    /// Copies the oldest frame into <paramref name="destination"/>; returns false while the queue does not play, which
    /// includes the call that finds it empty and makes it run dry.
    /// </summary>
    public bool TryDequeue(Span<short> destination)
    {
        if (!IsPlaying || _buffer is not { } buffer)
        {
            return false;
        }

        var now = _timeProvider.GetTimestamp();
        if (Count == 0)
        {
            RunDry(now);
            return false;
        }

        _windowMinimum = Math.Min(_windowMinimum, Count);
        TrimAtWindowEnd(now);
        buffer.AsSpan(_head * Samples, Samples).CopyTo(destination);
        DropOldest();
        ShrinkWhenSteady(now);
        return true;
    }

    /// <summary>
    /// Drops every queued frame and waits for <see cref="StartFrames"/> frames again before playing.
    /// </summary>
    public void Clear()
    {
        _head = 0;
        Count = 0;
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    private void DropOldest()
    {
        _head = (_head + 1) % Capacity;
        Count--;
    }

    // The fewest frames over a window is the delay that bursts did not cause, so dropping down to the start size there
    // removes standing delay without cutting into a burst that the following gap will consume.
    private void TrimAtWindowEnd(long now)
    {
        if (_timeProvider.GetElapsedTime(_windowStartTimestamp, now) < GameVoiceSettings.TrimWindow)
        {
            return;
        }

        if (_windowMinimum > StartFrames + GameVoiceSettings.TrimHeadroom)
        {
            Trims++;
            for (var index = StartFrames; index < _windowMinimum; index++)
            {
                DropOldest();
            }
        }

        StartWindow(now);
    }

    private void StartWindow(long now)
    {
        _windowStartTimestamp = now;
        _windowMinimum = int.MaxValue;
    }

    private void RunDry(long now)
    {
        IsPlaying = false;
        Underruns++;
        StartFrames = Math.Min(Capacity, StartFrames + GameVoiceSettings.BufferGrowth);
        _lastDryTimestamp = now;
    }

    private void ShrinkWhenSteady(long now)
    {
        if (StartFrames > GameVoiceSettings.ServerQueueStart
            && _timeProvider.GetElapsedTime(_lastDryTimestamp, now) >= GameVoiceSettings.BufferShrinkDelay)
        {
            StartFrames--;
            _lastDryTimestamp = now;
        }
    }
}
