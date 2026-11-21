using Namotion.Interceptor.Tracking;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// A startup completion that records how many completion deferrals had been taken when all of them had
/// first been released, which is the moment a startup completion wait would have passed, and what a
/// probe read at that moment.
/// </summary>
internal sealed class StartupCompletionRecorder : IStartupCompletion
{
    /// <summary>
    /// A leaf lock, so the count a release reads and the deferral that may race it are one step. Nothing
    /// but the probe is called while it is held.
    /// </summary>
    private readonly Lock _lock = new();

    private int _outstanding;
    private int _deferralCount;
    private int? _deferralCountWhenFirstSettled;
    private bool? _probeWhenFirstSettled;

    /// <summary>Read once, when no deferral is outstanding for the first time.</summary>
    public Func<bool>? Probe { get; set; }

    /// <summary>What <see cref="Probe"/> returned when none was outstanding for the first time, or null before that.</summary>
    public bool? ProbeWhenFirstSettled
    {
        get
        {
            lock (_lock)
            {
                return _probeWhenFirstSettled;
            }
        }
    }

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

    public int DeferralCount
    {
        get
        {
            lock (_lock)
            {
                return _deferralCount;
            }
        }
    }

    /// <summary>How many completion deferrals had been taken when none was outstanding for the first time, or null before that.</summary>
    public int? DeferralCountWhenFirstSettled
    {
        get
        {
            lock (_lock)
            {
                return _deferralCountWhenFirstSettled;
            }
        }
    }

    public IDisposable Defer()
    {
        lock (_lock)
        {
            _outstanding++;
            _deferralCount++;
        }

        return new Deferral(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            _outstanding--;
            if (_outstanding == 0 && _deferralCountWhenFirstSettled is null)
            {
                _deferralCountWhenFirstSettled = _deferralCount;
                _probeWhenFirstSettled = Probe?.Invoke();
            }
        }
    }

    private sealed class Deferral(StartupCompletionRecorder owner) : IDisposable
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
