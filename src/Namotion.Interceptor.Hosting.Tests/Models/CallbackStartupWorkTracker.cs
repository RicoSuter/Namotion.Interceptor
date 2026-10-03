using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup work tracker that counts its outstanding startup work and lets a test run code at the
/// moment it is tracked. The handler begins startup work after the attachment has been published and
/// before its start is enqueued, so the callback is the one piece of code that can drive that window
/// without a delay.
/// </summary>
public sealed class CallbackStartupWorkTracker : IStartupWorkTracker
{
    private int _outstanding;
    private int _taken;

    /// <summary>Invoked while startup work is being tracked. Exceptions are swallowed by the handler.</summary>
    public Action? OnTrack { get; set; }

    /// <summary>Startup work tracked but not yet ended.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Startup work tracked so far, which tells "ended again" apart from "never tracked".</summary>
    public int Taken => Volatile.Read(ref _taken);

    public IDisposable TrackStartupWork()
    {
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _taken);

        OnTrack?.Invoke();
        return new StartupWorkHandle(this);
    }

    private sealed class StartupWorkHandle : IDisposable
    {
        private readonly CallbackStartupWorkTracker _owner;
        private int _disposed;

        public StartupWorkHandle(CallbackStartupWorkTracker owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _owner._outstanding);
            }
        }
    }
}
