using System.Buffers;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// The frames of one speaker waiting for the mixer, in a pooled ring buffer. It plays once it holds its start size,
/// grows the start size by <see cref="GameVoiceSettings.BufferGrowth"/> when it runs dry while playing, and shrinks it
/// by one frame after <see cref="GameVoiceSettings.BufferShrinkDelay"/> of playing without running dry. It keeps at
/// most its start size plus one growth step, and never more than <see cref="GameVoiceSettings.ServerQueueMaximum"/>,
/// dropping the oldest frames. Not thread-safe: the mixer guards it with its lock.
/// </summary>
internal sealed class SpeakerQueue : IDisposable
{
    private const int Samples = GameVoiceSettings.SamplesPerFrame;
    private const int Capacity = GameVoiceSettings.ServerQueueMaximum;

    private readonly TimeProvider _timeProvider;
    private short[]? _buffer = ArrayPool<short>.Shared.Rent(Capacity * Samples);
    private int _head;
    private long _lastDryTimestamp;

    public SpeakerQueue(TimeProvider timeProvider)
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
    /// Adds a frame of exactly <see cref="GameVoiceSettings.FrameBytes"/> bytes.
    /// </summary>
    public void Enqueue(ReadOnlySpan<byte> frame)
    {
        if (_buffer is not { } buffer)
        {
            throw new ObjectDisposedException(nameof(SpeakerQueue));
        }

        var limit = Math.Min(Capacity, StartFrames + GameVoiceSettings.BufferGrowth);
        while (Count >= limit)
        {
            DropOldest();
        }

        var slot = (_head + Count) % Capacity;
        GameVoiceCodec.Decode(frame, buffer.AsSpan(slot * Samples, Samples));
        Count++;
        if (!IsPlaying && Count >= StartFrames)
        {
            IsPlaying = true;
            // The shrink delay counts only playing time, so a pause in speech does not shrink the start size.
            _lastDryTimestamp = _timeProvider.GetTimestamp();
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

        buffer.AsSpan(_head * Samples, Samples).CopyTo(destination);
        DropOldest();
        ShrinkWhenSteady(now);
        return true;
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

    private void RunDry(long now)
    {
        IsPlaying = false;
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
