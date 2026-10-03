using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup work tracker that throws from tracking startup work, from ending it, or both. Tracking
/// runs inside a property write, so an escape there surfaces at an unrelated assignment; ending runs in
/// a transition body's finally, so an escape there strands all startup work behind it and the host
/// waits on a completion that never comes. These switches are what make both observable.
/// </summary>
public sealed class ThrowingStartupWorkTracker : IStartupWorkTracker
{
    private int _taken;
    private int _released;

    /// <summary>Throws instead of returning a handle, so nothing is left to end.</summary>
    public bool ThrowOnTrack { get; init; }

    /// <summary>Returns a handle whose disposal throws, so the loop that ends startup work has to survive it.</summary>
    public bool ThrowOnRelease { get; init; }

    /// <summary>Calls that reached this tracker, whether or not they threw.</summary>
    public int Taken => Volatile.Read(ref _taken);

    /// <summary>Disposals that reached this tracker's handle, whether or not they threw.</summary>
    public int Released => Volatile.Read(ref _released);

    public IDisposable TrackStartupWork()
    {
        Interlocked.Increment(ref _taken);

        if (ThrowOnTrack)
        {
            throw new InvalidOperationException("tracking startup work failed");
        }

        return new StartupWorkHandle(this);
    }

    private sealed class StartupWorkHandle : IDisposable
    {
        private readonly ThrowingStartupWorkTracker _owner;

        public StartupWorkHandle(ThrowingStartupWorkTracker owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _owner._released);

            if (_owner.ThrowOnRelease)
            {
                throw new InvalidOperationException("ending startup work failed");
            }
        }
    }
}
