using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup completion that throws from deferring, from releasing a completion deferral, or both.
/// Deferring runs inside a property write, so an escape there surfaces at an unrelated assignment;
/// releasing runs in a transition body's finally, so an escape there strands every completion deferral
/// behind it and the host waits on a completion that never comes. These switches are what make both
/// observable.
/// </summary>
public sealed class ThrowingStartupCompletion : IStartupCompletion
{
    private int _deferralCount;
    private int _released;

    /// <summary>Throws instead of returning a handle, so nothing is left to release.</summary>
    public bool ThrowOnDefer { get; init; }

    /// <summary>Returns a handle whose disposal throws, so the loop that releases completion deferrals has to survive it.</summary>
    public bool ThrowOnRelease { get; init; }

    /// <summary>Calls that reached this startup completion, whether or not they threw.</summary>
    public int DeferralCount => Volatile.Read(ref _deferralCount);

    /// <summary>Disposals that reached this startup completion's handle, whether or not they threw.</summary>
    public int Released => Volatile.Read(ref _released);

    public IDisposable Defer()
    {
        Interlocked.Increment(ref _deferralCount);

        if (ThrowOnDefer)
        {
            throw new InvalidOperationException("deferring startup completion failed");
        }

        return new Deferral(this);
    }

    private sealed class Deferral : IDisposable
    {
        private readonly ThrowingStartupCompletion _owner;

        public Deferral(ThrowingStartupCompletion owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _owner._released);

            if (_owner.ThrowOnRelease)
            {
                throw new InvalidOperationException("releasing a completion deferral failed");
            }
        }
    }
}
