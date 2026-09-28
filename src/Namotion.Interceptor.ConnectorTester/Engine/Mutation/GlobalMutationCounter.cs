namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Process-wide monotonic counter used to make every mutation produce a globally
/// unique value, so the equality interceptor never suppresses a test mutation.
/// </summary>
public static class GlobalMutationCounter
{
    private static long _value;

    public static long Next() => Interlocked.Increment(ref _value);
}
