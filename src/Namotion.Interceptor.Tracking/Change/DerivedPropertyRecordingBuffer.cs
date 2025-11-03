using System.Buffers;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Pooled buffer for recording property dependencies during derived property evaluation.
/// Reuses buffers across recording sessions to avoid allocations in steady state.
/// </summary>
internal sealed class DerivedPropertyRecordingBuffer
{
    private readonly ArrayPool<PropertyReference> _pool = ArrayPool<PropertyReference>.Shared;
    private RecordingFrame[] _frames = [];
    private int _depth;

    private struct RecordingFrame
    {
        public PropertyReference[]? Buffer;
        public int Count;
    }

    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording => _depth > 0;

    /// <summary>
    /// Starts a new recording session (reuses existing frame buffer if available).
    /// </summary>
    public void StartRecording()
    {
        // Grow frame array if needed
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, Math.Max(2, _frames.Length * 2));
        }

        ref var frame = ref _frames[_depth++];
        frame.Count = 0;

        // Rent buffer if frame doesn't have one yet (first time or after growth)
        frame.Buffer ??= _pool.Rent(8); // Most properties have < 8 dependencies
    }

    /// <summary>
    /// Records a property access (automatically deduplicates).
    /// </summary>
    public void TouchProperty(ref PropertyReference property)
    {
        ref var frame = ref _frames[_depth - 1];
        var buffer = frame.Buffer!;

        // Check if already recorded (deduplication)
        for (int i = 0; i < frame.Count; i++)
        {
            if (buffer[i] == property)
                return; // Already recorded
        }

        // Grow buffer if needed
        if (frame.Count == buffer.Length)
        {
            var newBuffer = _pool.Rent(buffer.Length * 2);
            Array.Copy(buffer, newBuffer, frame.Count);
            _pool.Return(buffer, clearArray: false);
            frame.Buffer = newBuffer;
            buffer = newBuffer;
        }

        buffer[frame.Count++] = property;
    }

    /// <summary>
    /// Completes the recording session and returns recorded dependencies as a span.
    /// The span is only valid until the next recording session on this thread.
    /// </summary>
    public ReadOnlySpan<PropertyReference> FinishRecording()
    {
        _depth--;
        ref var frame = ref _frames[_depth];

        // Return span of recorded dependencies (buffer stays in frame for reuse)
        return new ReadOnlySpan<PropertyReference>(frame.Buffer, 0, frame.Count);
    }
}
