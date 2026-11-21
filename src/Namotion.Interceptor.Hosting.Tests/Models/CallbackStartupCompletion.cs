using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup completion that counts its outstanding completion deferrals and lets a test run code at
/// the moment one is taken. The handler defers startup completion after the attachment has been
/// published and before its start is enqueued, so the callback is the one piece of code that can drive
/// that window without a delay.
/// </summary>
public sealed class CallbackStartupCompletion : IStartupCompletion
{
    private int _outstanding;
    private int _deferralCount;

    /// <summary>Invoked while startup completion is being deferred. Exceptions are swallowed by the handler.</summary>
    public Action? OnDefer { get; set; }

    /// <summary>Completion deferrals taken but not yet released.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Completion deferrals taken so far, which tells "released again" apart from "never deferred".</summary>
    public int DeferralCount => Volatile.Read(ref _deferralCount);

    public IDisposable Defer()
    {
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _deferralCount);

        OnDefer?.Invoke();
        return new Deferral(this);
    }

    private sealed class Deferral : IDisposable
    {
        private readonly CallbackStartupCompletion _owner;
        private int _disposed;

        public Deferral(CallbackStartupCompletion owner)
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
