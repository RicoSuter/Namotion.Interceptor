namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Counts the exceptions thrown in the process, caught or not, from the first time it is read.
/// </summary>
internal static class FirstChanceExceptionCounter
{
    private static long _count;

    static FirstChanceExceptionCounter()
    {
        // Subscribed once for the life of the process, so the handler is never removed.
        AppDomain.CurrentDomain.FirstChanceException += (_, _) => Interlocked.Increment(ref _count);
    }

    public static long Count => Interlocked.Read(ref _count);
}
