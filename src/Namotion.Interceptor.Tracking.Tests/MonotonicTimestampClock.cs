using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Test-assembly-wide clock installed as <see cref="SubjectChangeContext.GetTimestampFunction"/>
/// via <see cref="ModuleInitializerAttribute"/>. Returns strictly monotonically increasing
/// timestamps (one tick beyond the previous return, or <see cref="DateTime.UtcNow"/> if larger),
/// so tests that need distinct timestamps for sequential captures get them without per-test
/// mocking of the static function. Eliminates the cross-test-class race window that thread-aware
/// per-test mocks could not fully close, since two parallel test classes can no longer overwrite
/// each other's installed function.
///
/// <see cref="CurrentThreadCount"/> exposes how many times the clock was captured on the calling
/// thread. Tests that need to assert the number of captures (e.g. lazy-cache verification, cascade
/// snap counting) read this before and after the work and diff; the counter is <c>[ThreadStatic]</c>
/// so concurrent tests on other threads cannot pollute it.
/// </summary>
internal static class MonotonicTimestampClock
{
    private static long _lastTicks;

    [ThreadStatic] private static int _threadCount;

    public static int CurrentThreadCount => _threadCount;

    public static DateTimeOffset Capture()
    {
        _threadCount++;
        long prev, next;
        do
        {
            prev = Volatile.Read(ref _lastTicks);
            var now = DateTime.UtcNow.Ticks;
            next = Math.Max(now, prev + 1);
        }
        while (Interlocked.CompareExchange(ref _lastTicks, next, prev) != prev);
        return new DateTimeOffset(next, TimeSpan.Zero);
    }
}

internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        SubjectChangeContext.GetTimestampFunction = MonotonicTimestampClock.Capture;
    }
}
