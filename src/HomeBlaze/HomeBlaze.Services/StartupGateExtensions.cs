using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

/// <summary>
/// Awaits the <see cref="StartupGate"/> of a subject context.
/// </summary>
public static class StartupGateExtensions
{
    /// <summary>
    /// Completes once the <see cref="StartupGate"/> of <paramref name="context"/> has completed, right away when
    /// the context has none. Faults or is cancelled like <see cref="StartupGate.Completed"/>.
    /// </summary>
    /// <remarks>
    /// Never await it where startup waits on the caller, or the two wait on each other: not while holding a
    /// startup deferral, not under a storage's lock (for example from <c>IConfigurable.ApplyConfigurationAsync</c>,
    /// which storages call under it, while a pending placeholder upgrade needs that lock), and not from an
    /// <c>IHostedService.StartAsync</c> that runs on the hosted service loop, which then cannot run the queued
    /// starts that defer startup. A <c>BackgroundService.ExecuteAsync</c> is fine.
    /// </remarks>
    public static Task WaitForStartupAsync(this IInterceptorSubjectContext context, CancellationToken cancellationToken)
    {
        return context.TryGetService<StartupGate>()?.Completed.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Whether the <see cref="StartupGate"/> of <paramref name="context"/> has completed, faulted or been
    /// cancelled. True when the context has none.
    /// </summary>
    public static bool IsStartupCompleted(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<StartupGate>()?.Completed.IsCompleted ?? true;
    }

    /// <summary>
    /// Defers every <see cref="IStartupCompletion"/> registered in <paramref name="context"/> until the returned
    /// handle is disposed. Take it before queueing the work and dispose it once the work ran, also on failure.
    /// </summary>
    public static IDisposable DeferStartupCompletion(this IInterceptorSubjectContext context)
    {
        var startupCompletions = context.GetServices<IStartupCompletion>();
        if (startupCompletions.IsEmpty)
        {
            return NoOpDeferral.Instance;
        }

        if (startupCompletions.Length == 1)
        {
            return startupCompletions[0].Defer();
        }

        var deferrals = new IDisposable[startupCompletions.Length];
        for (var index = 0; index < deferrals.Length; index++)
        {
            deferrals[index] = startupCompletions[index].Defer();
        }

        return new CompositeDeferral(deferrals);
    }

    private sealed class CompositeDeferral(IDisposable[] deferrals) : IDisposable
    {
        private IDisposable[]? _deferrals = deferrals;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _deferrals, null) is { } deferrals)
            {
                foreach (var deferral in deferrals)
                {
                    deferral.Dispose();
                }
            }
        }
    }
}
