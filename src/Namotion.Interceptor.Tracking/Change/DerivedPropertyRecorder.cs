using System.Buffers;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>A property read during an evaluation, with the source's version captured before the read.</summary>
internal readonly record struct RecordedDependency(PropertyReference Property, DerivedPropertyData Data, long Version);

/// <summary>
/// Records the reads of a derived getter. Installed as the thread's read observer while at least one
/// recording runs, so reads of subjects without a context are recorded as well. Frames nest; a
/// suspended frame records nothing.
/// </summary>
internal sealed class DerivedPropertyRecorder : IPropertyReadObserver
{
    [ThreadStatic]
    private static DerivedPropertyRecorder? _current;

    private readonly ArrayPool<RecordedDependency> _pool = ArrayPool<RecordedDependency>.Shared;
    private RecordingFrame[] _frames = [];
    private int _depth;

    private struct RecordingFrame
    {
        public PropertyReference Property;
        public bool IsSuspended;
        public RecordedDependency[]? Buffer;
        public int Count;
        public bool HasOwnWriteAfterRead;
    }

    internal static DerivedPropertyRecorder Current => _current ??= new DerivedPropertyRecorder();

    internal static bool IsRecordingOnThisThread => _current is { IsRecording: true };

    public bool IsRecording => _depth > 0 && !_frames[_depth - 1].IsSuspended;

    /// <summary>A getter is being evaluated on this thread, also while its recording is suspended.</summary>
    internal static bool IsEvaluatingOnThisThread => _current is { _depth: > 0 };

    /// <summary>
    /// Starts a recording frame for the getter of <paramref name="property"/> (reuses a pooled buffer; allocation-free in
    /// steady state). Reads of <paramref name="property"/> itself are filtered so a derived property never depends on
    /// itself: a dynamic derived getter reads its own property again through the interceptor chain.
    /// </summary>
    public void StartRecording(in PropertyReference property)
    {
        PushFrame(property, isSuspended: false);
    }

    /// <summary>
    /// Records nothing on this thread until <see cref="Resume"/>; a recording started inside records normally.
    /// Returns false, and suspends nothing, when no recording runs on this thread.
    /// </summary>
    internal static bool TrySuspend()
    {
        var recorder = _current;
        if (recorder is null || recorder._depth == 0)
        {
            return false;
        }

        recorder.PushFrame(default, isSuspended: true);
        return true;
    }

    /// <summary>Ends the suspension of a <see cref="TrySuspend"/> that returned true.</summary>
    internal static void Resume()
    {
        var recorder = _current!;
        recorder.FinishRecording();
        recorder.ClearLastRecording();
    }

    private void PushFrame(in PropertyReference property, bool isSuspended)
    {
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, Math.Max(2, _frames.Length * 2));
        }

        ref var frame = ref _frames[_depth++];
        frame.Count = 0;
        frame.Property = property;
        frame.IsSuspended = isSuspended;
        frame.HasOwnWriteAfterRead = false;
        if (!isSuspended)
        {
            frame.Buffer ??= _pool.Rent(8);
        }

        // The read observer is a single slot: a recording replaces any other observer of this thread, and
        // FinishRecording clears it instead of restoring one.
        PropertyAccessObservation.ReadObserver = this;
    }

    public void OnPropertyRead(in PropertyReference property)
    {
        ref var frame = ref _frames[_depth - 1];
        if (frame.IsSuspended || property == frame.Property)
        {
            return;
        }

        var buffer = frame.Buffer!;
        for (var i = 0; i < frame.Count; i++)
        {
            if (buffer[i].Property == property)
            {
                // The first read keeps its version: a later version would let a value mixed from
                // before and after a write pass the version check.
                return;
            }
        }

        // R1 and R2 of the protocol: the data exists and is fenced before its version is read with
        // acquire semantics, and the version is read before the value.
        var data = property.GetDerivedPropertyData();
        Interlocked.MemoryBarrier();
        var version = Volatile.Read(ref data.Version);

        if (frame.Count == buffer.Length)
        {
            var newBuffer = _pool.Rent(buffer.Length * 2);
            Array.Copy(buffer, newBuffer, frame.Count);
            _pool.Return(buffer, clearArray: true);
            frame.Buffer = newBuffer;
            buffer = newBuffer;
        }

        buffer[frame.Count++] = new RecordedDependency(property, data, version);
    }

    /// <summary>
    /// Called after a write on this thread moved a property's version to <paramref name="newVersion"/>. A
    /// recording of this thread that read the property at the version just before takes the new version, so the
    /// thread's own write does not fail the version check, and is marked (<see cref="LastRecordingHasOwnWriteAfterRead"/>):
    /// its value may stem from before the write. A write of another thread in between moved the version further:
    /// that recording keeps its version and still fails the check.
    /// </summary>
    internal void OnOwnWrite(in PropertyReference property, long newVersion)
    {
        for (var depth = 0; depth < _depth; depth++)
        {
            ref var frame = ref _frames[depth];
            if (!frame.IsSuspended)
            {
                RebaseOnOwnWrite(ref frame, property, newVersion);
            }
        }
    }

    private static void RebaseOnOwnWrite(ref RecordingFrame frame, in PropertyReference property, long newVersion)
    {
        var buffer = frame.Buffer!;
        for (var i = 0; i < frame.Count; i++)
        {
            if (buffer[i].Property == property)
            {
                if (buffer[i].Version == newVersion - 1)
                {
                    buffer[i] = buffer[i] with { Version = newVersion };
                    frame.HasOwnWriteAfterRead = true;
                }

                return;
            }
        }
    }

    /// <summary>
    /// Ends the innermost frame and returns its reads. The span (and <see cref="LastRecording"/>) stays valid until
    /// <see cref="ClearLastRecording"/> or the next <see cref="StartRecording"/> on this thread, which the version
    /// check of a commit relies on.
    /// </summary>
    public ReadOnlySpan<RecordedDependency> FinishRecording()
    {
        _depth--;
        if (_depth == 0)
        {
            PropertyAccessObservation.ReadObserver = null;
        }

        ref var frame = ref _frames[_depth];
        return new ReadOnlySpan<RecordedDependency>(frame.Buffer, 0, frame.Count);
    }

    /// <summary>
    /// The reads of the recording finished last on this thread; valid after <see cref="FinishRecording"/> until
    /// <see cref="ClearLastRecording"/> or the next <see cref="StartRecording"/>.
    /// </summary>
    internal ReadOnlySpan<RecordedDependency> LastRecording => new(_frames[_depth].Buffer, 0, _frames[_depth].Count);

    /// <summary>
    /// A write on this thread moved a property that the recording finished last had read before, without a write of
    /// another thread in between; valid like <see cref="LastRecording"/>.
    /// </summary>
    internal bool LastRecordingHasOwnWriteAfterRead => _frames[_depth].HasOwnWriteAfterRead;

    /// <summary>
    /// Clears the reads of the recording finished last, so the thread-static recorder holds no references to
    /// subjects that may be detached. Called once its span is no longer in use.
    /// </summary>
    public void ClearLastRecording()
    {
        // No recording finished at this depth yet (a failure before the first recording on this thread).
        if (_depth >= _frames.Length)
        {
            return;
        }

        ref var frame = ref _frames[_depth];
        if (frame.Count > 0)
        {
            Array.Clear(frame.Buffer!, 0, frame.Count);
            frame.Count = 0;
        }

        frame.Property = default;
        frame.IsSuspended = false;
        frame.HasOwnWriteAfterRead = false;
    }
}
