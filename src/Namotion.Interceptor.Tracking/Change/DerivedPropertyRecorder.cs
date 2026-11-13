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
    }

    internal static DerivedPropertyRecorder Current => _current ??= new DerivedPropertyRecorder();

    internal static bool IsRecordingOnThisThread => _current is { IsRecording: true };

    public bool IsRecording => _depth > 0 && !_frames[_depth - 1].IsSuspended;

    /// <summary>A getter is being evaluated on this thread, also while its recording is suspended.</summary>
    internal static bool IsEvaluatingOnThisThread => _current is { _depth: > 0 };

    public void StartRecording(in PropertyReference property)
    {
        PushFrame(property, isSuspended: false);
    }

    /// <summary>Records nothing until disposed; a recording started inside records normally.</summary>
    internal static SuspendedRecording Suspend()
    {
        var recorder = _current;
        if (recorder is null || recorder._depth == 0)
        {
            return default;
        }

        recorder.PushFrame(default, isSuspended: true);
        return new SuspendedRecording(recorder);
    }

    internal readonly ref struct SuspendedRecording(DerivedPropertyRecorder? recorder)
    {
        public void Dispose()
        {
            if (recorder is not null)
            {
                recorder.FinishRecording();
                recorder.ClearLastRecording();
            }
        }
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
        frame.Buffer ??= _pool.Rent(8);
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
    }
}
