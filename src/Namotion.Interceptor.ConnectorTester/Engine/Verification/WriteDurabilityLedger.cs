using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
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
    private readonly ISubjectSource? _source;

    // Written by Reset and read by DroppedWriteCount, which only the verification loop calls, one after the other.
    private long _droppedWriteBaseline;

    /// <summary>
    /// Creates a ledger for a participant whose writes go through <paramref name="source"/>, or for a participant
    /// without a source when it is <c>null</c>.
    /// </summary>
    public WriteDurabilityLedger(ISubjectSource? source = null)
    {
        _source = source;
    }

    /// <summary>
    /// Gets the number of writes the participant's source dropped, as counted by its retry queue's
    /// <c>TotalDropped</c>, since the last <see cref="Reset"/>.
    /// </summary>
    public long DroppedWriteCount => ReadTotalDropped() - _droppedWriteBaseline;

    /// <summary>
    /// Gets whether the participant's source is <see cref="SourceState.Synchronized"/>, and <c>true</c> for a
    /// participant without a source.
    /// </summary>
    /// <remarks>
    /// Reset the ledger for the first time only once this has been <c>true</c>: a write made before the source claimed its properties is
    /// discarded without being counted as dropped.
    /// </remarks>
    public bool IsSourceSynchronized => _source is null || _source.State == SourceState.Synchronized;

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
        }

        _droppedWriteBaseline = ReadTotalDropped();
    }

    /// <summary>
    /// Returns one line per recorded write the model no longer holds: the property path with the written value and the
    /// value the model holds, or with "no longer in the model" for a node missing from <paramref name="reachableNodes"/>,
    /// the participant's current graph.
    /// </summary>
    public IReadOnlyList<string> Verify(IReadOnlyCollection<TestNode> reachableNodes)
    {
        var reachable = new HashSet<TestNode>(reachableNodes);
        var violations = new List<string>();

        lock (_lock)
        {
            foreach (var (node, (property, counter)) in _lastWrites)
            {
                if (!reachable.Contains(node))
                {
                    violations.Add($"{GetPath(node, property)}: no longer in the model");
                    continue;
                }

                var (written, held) = TestNode.ReadValueProperty(node, property, counter);
                if (!Equals(written, held))
                {
                    violations.Add($"{GetPath(node, property)}: wrote '{written}', model holds '{held}'");
                }
            }
        }

        return violations;
    }

    private static string GetPath(TestNode node, int property)
    {
        var name = TestNode.GetValuePropertyName(property);
        return node.TryGetRegisteredProperty(name)?.TryGetPath() ?? name;
    }

    private long ReadTotalDropped() => _source?.Diagnostics.OutboundRetries.TotalDropped ?? 0;
}
