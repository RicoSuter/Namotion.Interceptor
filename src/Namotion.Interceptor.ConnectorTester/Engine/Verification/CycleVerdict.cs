using Namotion.Interceptor.ConnectorTester.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Decides the result of a verification cycle.
/// </summary>
public static class CycleVerdict
{
    /// <summary>
    /// Returns <see cref="CycleResult.Pass"/> when the participants converged, no mutation engine counted an
    /// unexpected commit failure in the cycle and the write-durability check found no lost write, and
    /// <see cref="CycleResult.Fail"/> otherwise.
    /// </summary>
    public static CycleResult Decide(
        bool converged, IEnumerable<MutationEngine> mutationEngines, int writeDurabilityViolationCount)
    {
        return converged &&
            writeDurabilityViolationCount == 0 &&
            mutationEngines.All(engine => engine.UnexpectedFailedCommitCount == 0)
            ? CycleResult.Pass
            : CycleResult.Fail;
    }
}
