using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// A startup work tracker that records how much startup work had been tracked when all of it had first
/// ended, which is the moment a startup completion wait would have passed.
/// </summary>
internal sealed class StartupWorkRecorder : IStartupWorkTracker
{
    // A leaf lock, so the count an end reads and the tracking that may race it are one step.
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

    /// <summary>How much startup work had been tracked when none was outstanding for the first time, or null before that.</summary>
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

    public IDisposable TrackStartupWork()
    {
        lock (_lock)
        {
            _outstanding++;
            _taken++;
        }

        return new StartupWorkHandle(this);
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

    private sealed class StartupWorkHandle(StartupWorkRecorder owner) : IDisposable
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
