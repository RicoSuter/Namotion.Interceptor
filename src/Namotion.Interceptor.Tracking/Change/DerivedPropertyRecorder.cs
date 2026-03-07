using System.Buffers;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Pooled buffer for recording property dependencies during derived property evaluation.
///
/// - Stack-based: Supports nested derived property evaluations (rare but possible)
/// - Pooled: Reuses ArrayPool buffers to avoid allocations in steady state
/// - Deduplication: Automatically removes duplicate property accesses
///
/// Lifecycle: StartRecording() → TouchProperty() (N times) → FinishRecording()
/// </summary>
internal sealed class DerivedPropertyRecorder
{
    private readonly ArrayPool<PropertyReference> _pool = ArrayPool<PropertyReference>.Shared;
    
    private RecordingFrame[] _frames = [];
    private int _depth; // Current nesting level (usually 0 or 1)

    // Frame holds buffer and current count for one recording session
    private struct RecordingFrame
    {
        public PropertyReference[]? Buffer; // Rented from pool, reused across sessions
        public int Count;                   // Number of recorded items in this session
    }

    /// <summary>
    /// Gets whether recording is currently active (depth > 0).
    /// </summary>
    public bool IsRecording => _depth > 0;

    /// <summary>
    /// Starts a new recording session. Reuses existing pooled buffer if available (allocation-free steady state).
    /// </summary>
    public void StartRecording()
    {
        // Grow frame stack if needed (rare - only happens on first use or deep nesting)
        if (_depth == _frames.Length)
            Array.Resize(ref _frames, Math.Max(2, _frames.Length * 2));

        // Get frame for this depth level and reset count
        ref var frame = ref _frames[_depth++];
        frame.Count = 0;

        // Rent pooled buffer if not already allocated (allocation-free on subsequent calls)
        frame.Buffer ??= _pool.Rent(8); // Typical derived property has < 8 dependencies
    }

    /// <summary>
    /// Records a property access during derived property evaluation. Automatically deduplicates.
    /// <para><b>Example:</b> If property X accessed twice in same getter, only recorded once.</para>
    /// </summary>
    public void TouchProperty(ref PropertyReference property)
    {
        ref var frame = ref _frames[_depth - 1];
        var buffer = frame.Buffer!;

        // Deduplicate: Skip if already recorded in this session
        if (Array.IndexOf(buffer, property, 0, frame.Count) >= 0)
            return;

        // Grow buffer if full (rare - only if property has > 8, 16, 32... dependencies)
        if (frame.Count == buffer.Length)
        {
            var newBuffer = _pool.Rent(buffer.Length * 2);
            Array.Copy(buffer, newBuffer, frame.Count);
            _pool.Return(buffer, clearArray: true);
            frame.Buffer = newBuffer;
            buffer = newBuffer;
        }

        // Record dependency
        buffer[frame.Count++] = property;
    }

    /// <summary>
    /// Completes recording session and returns recorded dependencies as a span.
    /// <para><b>Important:</b> Span is only valid until <see cref="ClearLastRecording"/> or next
    /// <see cref="StartRecording"/> call on this thread.</para>
    /// </summary>
    public ReadOnlySpan<PropertyReference> FinishRecording()
    {
        _depth--;
        ref var frame = ref _frames[_depth];

        // Return span view into pooled buffer (zero allocation)
        return new ReadOnlySpan<PropertyReference>(frame.Buffer, 0, frame.Count);
    }

    /// <summary>
    /// Clears stale PropertyReference values from the last finished recording's buffer.
    /// Must be called after the span from <see cref="FinishRecording"/> is no longer in use.
    /// Prevents the thread-static recorder from holding strong references to detached subjects.
    /// </summary>
    public void ClearLastRecording()
    {
        ref var frame = ref _frames[_depth];
        if (frame.Count > 0)
        {
            Array.Clear(frame.Buffer!, 0, frame.Count);
            frame.Count = 0;
        }
    }
}
