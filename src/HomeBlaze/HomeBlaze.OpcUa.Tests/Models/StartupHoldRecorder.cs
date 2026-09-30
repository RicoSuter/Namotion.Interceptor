using Namotion.Interceptor.Tracking;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// A startup completion deferrer that records how many holds had been taken when every hold was first
/// released, which is the moment a startup completion wait would have passed.
/// </summary>
internal sealed class StartupHoldRecorder : IStartupCompletionDeferrer
{
    /// <summary>
    /// A leaf lock, so the count a release reads and the take that may race it are one step. Nothing is
    /// called while it is held.
    /// </summary>
    private readonly Lock _lock = new();

    private int _outstanding;
    private int _taken;
    private int? _takenWhenFirstSettled;

    public int Outstanding
    {
        get
        {
            lock (_lock)
            {
                return _outstanding;
            }
        }
    }

    public int Taken
    {
        get
        {
            lock (_lock)
            {
                return _taken;
            }
        }
    }

    /// <summary>How many holds had been taken when none was outstanding for the first time, or null before that.</summary>
    public int? TakenWhenFirstSettled
    {
        get
        {
            lock (_lock)
            {
                return _takenWhenFirstSettled;
            }
        }
    }

    public IDisposable DeferWaitCompletion()
    {
        lock (_lock)
        {
            _outstanding++;
            _taken++;
        }

        return new Hold(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            _outstanding--;
            if (_outstanding == 0)
            {
                _takenWhenFirstSettled ??= _taken;
            }
        }
    }

    private sealed class Hold(StartupHoldRecorder owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
