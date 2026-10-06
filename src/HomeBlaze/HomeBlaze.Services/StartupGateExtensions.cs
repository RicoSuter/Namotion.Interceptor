using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

/// <summary>
/// Defers and awaits startup completion through the services of a subject context.
/// </summary>
public static class StartupGateExtensions
{
    /// <summary>
    /// Defers every <see cref="IStartupCompletion"/> reachable from <paramref name="context"/> until the returned
    /// handle is disposed. Take it before queueing the work and dispose it once the work ran, also on failure.
    /// Disposing it more than once releases the deferrals once.
    /// </summary>
    public static IDisposable DeferStartupCompletion(this IInterceptorSubjectContext context)
    {
        var startupCompletions = context.GetServices<IStartupCompletion>();
        if (startupCompletions.IsEmpty)
        {
            return EmptyDeferral.Instance;
        }

        var deferrals = new IDisposable[startupCompletions.Length];
        var count = 0;
        try
        {
            for (; count < startupCompletions.Length; count++)
            {
                deferrals[count] = startupCompletions[count].Defer();
            }
        }
        catch
        {
            ReleaseAll(deferrals.AsSpan(0, count));
            throw;
        }

        return new StartupDeferral(deferrals);
    }

    /// <summary>
    /// Completes once the <see cref="StartupGate"/> of <paramref name="context"/> has completed, right away when
    /// the context has none. Faults or is cancelled like <see cref="StartupGate.Completed"/>.
    /// </summary>
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

    private static void ReleaseAll(ReadOnlySpan<IDisposable> deferrals)
    {
        List<Exception>? exceptions = null;
        foreach (var deferral in deferrals)
        {
            // One deferral throwing must not strand the others: a leaked deferral keeps startup open forever.
            try
            {
                deferral.Dispose();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException(exceptions);
        }
    }

    private sealed class StartupDeferral(IDisposable[] deferrals) : IDisposable
    {
        private IDisposable[]? _deferrals = deferrals;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _deferrals, null) is { } deferralsToRelease)
            {
                ReleaseAll(deferralsToRelease);
            }
        }
    }

    private sealed class EmptyDeferral : IDisposable
    {
        public static readonly EmptyDeferral Instance = new();

        public void Dispose()
        {
        }
    }
}
