using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Records the property and mutation counter of the last value a participant wrote to each <c>TestNode</c> and checks,
/// once the run has quiesced, that the participant's own model still holds the value
/// <see cref="TestNode.WriteValueProperty"/> writes for that counter.
/// </summary>
/// <remarks>
/// Sound only when the recording participant writes a single property per node and is its sole writer
/// (<see cref="ConnectorTesterConfiguration.VerifyWriteDurability"/>); otherwise another participant's later write
/// is reported as a loss.
/// </remarks>
public sealed class WriteDurabilityLedger
{
    private readonly Dictionary<TestNode, (int Property, long Counter)> _lastWrites = new();
    private readonly Lock _lock = new();
    private readonly Func<long>? _droppedWriteCount;
    private long _droppedWriteBaseline;

    /// <summary>
    /// Creates a ledger. <paramref name="droppedWriteCount"/> returns the running total of writes the participant's
    /// source dropped, such as its retry queue's <c>TotalDropped</c>, and is <c>null</c> for a participant without
    /// a source.
    /// </summary>
    public WriteDurabilityLedger(Func<long>? droppedWriteCount = null)
    {
        _droppedWriteCount = droppedWriteCount;
        _droppedWriteBaseline = droppedWriteCount?.Invoke() ?? 0;
    }

    /// <summary>Gets the number of writes the participant's source dropped since the last <see cref="Reset"/>.</summary>
    public long DroppedWriteCount => (_droppedWriteCount?.Invoke() ?? 0) - Interlocked.Read(ref _droppedWriteBaseline);

    public void Record(TestNode node, int property, long counter)
    {
        lock (_lock)
        {
            _lastWrites[node] = (property, counter);
        }
    }

    /// <summary>
    /// Records the <paramref name="writes"/> of one transaction, in write order: all of them when the commit succeeded
    /// (<paramref name="failure"/> is <c>null</c>), and otherwise only those whose property is in the failure's
    /// <see cref="SubjectTransactionException.AppliedChanges"/>, because the model does not hold the others.
    /// </summary>
    public void RecordCommit(
        IReadOnlyList<(TestNode Node, int Property, long Counter)> writes,
        SubjectTransactionException? failure)
    {
        HashSet<PropertyReference>? appliedProperties = null;
        if (failure is not null)
        {
            appliedProperties = new HashSet<PropertyReference>(failure.AppliedChanges.Count);
            foreach (var change in failure.AppliedChanges)
            {
                appliedProperties.Add(change.Property);
            }
        }

        lock (_lock)
        {
            foreach (var (node, property, counter) in writes)
            {
                if (appliedProperties is null ||
                    appliedProperties.Contains(new PropertyReference(node, TestNode.GetValuePropertyName(property))))
                {
                    _lastWrites[node] = (property, counter);
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastWrites.Clear();
            Interlocked.Exchange(ref _droppedWriteBaseline, _droppedWriteCount?.Invoke() ?? 0);
        }
    }

    /// <summary>
    /// Returns one line per recorded write that a node in <paramref name="reachableNodes"/>, the participant's current
    /// graph, no longer holds, naming the property path, the written value and the value the model holds.
    /// </summary>
    public IReadOnlyList<string> Verify(IReadOnlyCollection<TestNode> reachableNodes)
    {
        var reachable = new HashSet<TestNode>(reachableNodes);
        var violations = new List<string>();

        lock (_lock)
        {
            foreach (var (node, (property, counter)) in _lastWrites)
            {
                // A node no longer in the graph, removed locally or by another participant, carries no durability claim.
                if (!reachable.Contains(node))
                {
                    continue;
                }

                var (name, written, held) = TestNode.ReadValueProperty(node, property, counter);
                if (!Equals(written, held))
                {
                    var path = node.TryGetRegisteredProperty(name)?.TryGetPath() ?? name;
                    violations.Add($"{path}: wrote '{written}', model holds '{held}'");
                }
            }
        }

        return violations;
    }
}
