using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// A startup completion that records how many completion deferrals had been taken when all of them had
/// first been released, which is the moment a startup completion wait would have passed.
/// </summary>
internal sealed class StartupCompletionRecorder : IStartupCompletion
{
    // A leaf lock, so the count a release reads and the deferral that may race it are one step.
    private readonly Lock _lock = new();

    private int _outstanding;
    private int _deferralCount;
    private int? _deferralCountWhenFirstSettled;

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
            if (_outstanding == 0)
            {
                _deferralCountWhenFirstSettled ??= _deferralCount;
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
