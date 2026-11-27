using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Waits for the participants' sources to synchronize, so the write-durability check starts counting only once every
/// source has claimed its properties.
/// </summary>
public static class SourceSynchronization
{
    /// <summary>
    /// Polls every <paramref name="pollInterval"/> until each engine has reported
    /// <see cref="MutationEngine.IsSourceSynchronized"/> at least once or <paramref name="timeout"/> elapses, checking
    /// at least once. Returns the names of the engines whose source was never seen synchronized, empty when all were.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WaitAsync(
        IReadOnlyCollection<MutationEngine> mutationEngines,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var pending = mutationEngines.ToList();
        while (true)
        {
            // Once is enough: a source keeps its claim on its properties across reconnects, so chaos knocking it
            // back to synchronizing afterwards does not matter.
            pending.RemoveAll(engine => engine.IsSourceSynchronized);
            if (pending.Count == 0 || stopwatch.Elapsed >= timeout)
            {
                return pending.Select(engine => engine.Name).ToList();
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }
}
