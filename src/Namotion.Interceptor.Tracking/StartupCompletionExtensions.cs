using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Defers startup completion through the services of a subject context.
/// </summary>
public static class StartupCompletionExtensions
{
    /// <summary>
    /// Defers every <see cref="IStartupCompletion"/> registered in <paramref name="context"/> until the returned
    /// handle is disposed. Take it before queueing the work and dispose it once the work ran, also on failure.
    /// </summary>
    /// <remarks>
    /// Disposing the handle more than once, also concurrently, releases the deferrals once. If releasing one
    /// deferral throws, the others are still released and the failure is rethrown afterwards, several failures
    /// as an <see cref="AggregateException"/>. If an <see cref="IStartupCompletion.Defer"/> throws, the
    /// deferrals already taken are released before the exception propagates.
    /// </remarks>
    /// <param name="context">The context whose startup completions to defer.</param>
    /// <returns>The handle that releases the deferrals; a shared instance when the context has no startup completion.</returns>
    public static IDisposable DeferStartupCompletion(this IInterceptorSubjectContext context)
    {
        var startupCompletions = context.GetServices<IStartupCompletion>();
        if (startupCompletions.IsEmpty)
        {
            return NoStartupDeferral.Instance;
        }

        if (startupCompletions.Length == 1)
        {
            return new StartupDeferral(startupCompletions[0].Defer());
        }

        var deferrals = new IDisposable[startupCompletions.Length];
        var count = 0;
        try
        {
            for (; count < deferrals.Length; count++)
            {
                deferrals[count] = startupCompletions[count].Defer();
            }
        }
        catch (Exception exception)
        {
            var releaseExceptions = ReleaseAll(deferrals.AsSpan(0, count));
            if (releaseExceptions is not null)
            {
                releaseExceptions.Insert(0, exception);
                throw new AggregateException(releaseExceptions);
            }

            throw;
        }

        return new StartupDeferral(deferrals);
    }

    /// <summary>
    /// Disposes every deferral, also when one throws: a leaked deferral keeps startup open forever.
    /// </summary>
    private static List<Exception>? ReleaseAll(ReadOnlySpan<IDisposable> deferrals)
    {
        List<Exception>? exceptions = null;
        foreach (var deferral in deferrals)
        {
            try
            {
                deferral.Dispose();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return exceptions;
    }

    private sealed class NoStartupDeferral : IDisposable
    {
        public static readonly NoStartupDeferral Instance = new();

        private NoStartupDeferral()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StartupDeferral : IDisposable
    {
        // Either a single IDisposable or an IDisposable[], so the single-completion case allocates no array.
        private object? _deferrals;

        public StartupDeferral(object deferrals)
        {
            _deferrals = deferrals;
        }

        public void Dispose()
        {
            switch (Interlocked.Exchange(ref _deferrals, null))
            {
                case IDisposable deferral:
                    deferral.Dispose();
                    break;

                case IDisposable[] deferrals:
                    ThrowIfAny(ReleaseAll(deferrals));
                    break;
            }
        }

        private static void ThrowIfAny(List<Exception>? exceptions)
        {
            if (exceptions is { Count: 1 })
            {
                ExceptionDispatchInfo.Throw(exceptions[0]);
            }

            if (exceptions is not null)
            {
                throw new AggregateException(exceptions);
            }
        }
    }
}
