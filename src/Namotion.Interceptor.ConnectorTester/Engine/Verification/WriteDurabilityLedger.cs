using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Records the last value a participant wrote to each <c>TestNode</c> value property and checks, once the run
/// has quiesced, that the participant's own model still holds it.
/// </summary>
/// <remarks>
/// Sound only when the recording participant is the sole writer of each recorded property
/// (<see cref="ConnectorTesterConfiguration.DisjointProperties"/>); otherwise another participant's later write
/// is reported as a loss.
/// </remarks>
public sealed class WriteDurabilityLedger
{
    private readonly Dictionary<(TestNode Node, int Property), object> _lastWrites = new();
    private readonly Lock _lock = new();

    public void Record(TestNode node, int property, object value)
    {
        lock (_lock)
        {
            _lastWrites[(node, property)] = value;
        }
    }

    /// <summary>
    /// Records the <paramref name="writes"/> of one transaction, in write order: all of them when the commit succeeded
    /// (<paramref name="failure"/> is <c>null</c>), and otherwise only those whose property is in the failure's
    /// <see cref="SubjectTransactionException.AppliedChanges"/>, because the model does not hold the others.
    /// </summary>
    public void RecordCommit(
        IReadOnlyList<(TestNode Node, int Property, object Value)> writes,
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
            foreach (var (node, property, value) in writes)
            {
                if (appliedProperties is null ||
                    appliedProperties.Contains(new PropertyReference(node, TestNode.GetValuePropertyName(property))))
                {
                    _lastWrites[(node, property)] = value;
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
            foreach (var ((node, property), expected) in _lastWrites)
            {
                // A node no longer in the graph, removed locally or by another participant, carries no durability claim.
                if (!reachable.Contains(node))
                {
                    continue;
                }

                var (name, actual) = TestNode.ReadValueProperty(node, property);
                if (!Equals(expected, actual))
                {
                    var path = node.TryGetRegisteredProperty(name)?.TryGetPath() ?? name;
                    violations.Add($"{path}: wrote '{expected}', model holds '{actual}'");
                }
            }
        }

        return violations;
    }
}
