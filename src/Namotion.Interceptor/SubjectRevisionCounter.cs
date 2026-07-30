using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// Assigns the monotonic per-subject commit revision. Called by the terminal write with the
/// subject's SyncRoot held, which is what makes the plain increment safe.
/// </summary>
internal static class SubjectRevisionCounter
{
    private const string RevisionKey = "Namotion.Interceptor.Revision";

    /// <summary>
    /// Returns the next revision for the subject. Callers must hold the subject's SyncRoot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Next(IInterceptorSubject subject)
    {
        Debug.Assert(Monitor.IsEntered(subject.SyncRoot),
            "The revision counter must be incremented under the subject's SyncRoot: the plain increment on the fast path relies on that exclusion.");

        // Generated subjects own their executor, so the counter is a plain field on an object that
        // is already hot in this write: no lookup, no atomic, no shared cache line.
        if (subject.Context is InterceptorExecutor executor)
        {
            return ++executor.Revision;
        }

        return NextFallback(subject);
    }

    /// <summary>
    /// Hand-written subjects whose context is not an <see cref="InterceptorExecutor"/> keep the
    /// counter in subject data, mirroring the write-timestamp holder. Label only: ordered delivery
    /// is not offered for such subjects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long NextFallback(IInterceptorSubject subject)
    {
        var holder = (long[])subject.Data.GetOrAdd((null, RevisionKey), static _ => new long[1])!;

        // Hand-written subjects are the ones least likely to honour the SyncRoot contract, and a
        // 64-bit increment is not atomic on 32-bit runtimes anyway (netstandard2.0 covers x86
        // .NET Framework). Interlocked keeps the value dense without allocating.
        return Interlocked.Increment(ref holder[0]);
    }
}
