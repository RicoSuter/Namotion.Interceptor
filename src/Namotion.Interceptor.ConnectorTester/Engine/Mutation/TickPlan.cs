namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Computes the per-tick batch size and the delay (in milliseconds) between ticks
/// for a given mutations-per-second rate. Matches the formulas previously inlined
/// in MutationEngine.RunStructuralMutationsAsync and RandomMutationEngine.RunValueMutationsAsync.
/// </summary>
public readonly record struct TickPlan(int BatchSize, int DelayMs)
{
    public static TickPlan From(int ratePerSecond)
    {
        var safeRate = Math.Max(1, ratePerSecond);
        var batchSize = Math.Max(1, (int)Math.Ceiling(safeRate / 1000.0));
        var delayMs = Math.Max(1, (int)Math.Round(1000.0 * batchSize / safeRate));
        return new TickPlan(batchSize, delayMs);
    }
}
