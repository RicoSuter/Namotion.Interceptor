using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Records the last value a participant wrote to each property and checks, once the run has quiesced,
/// that the participant's own model still holds it.
/// </summary>
/// <remarks>
/// This is the only instrument that can see a lost client-to-server write. When such a write is lost,
/// the reconnect's complete-state load reverts the writer's own model to the peer's older value, so
/// both sides agree and a snapshot comparison passes while the write is gone. Sound only where the
/// recording participant is the sole writer of the property, which is what the disjoint-property
/// option guarantees; with overlapping writers a legitimate overwrite is indistinguishable from a loss.
/// </remarks>
public sealed class WriteDurabilityLedger
{
    private readonly Dictionary<(TestNode Node, int Property), object?> _lastWrites = new();
    private readonly Lock _lock = new();

    public void Record(TestNode node, int property, object? value)
    {
        lock (_lock)
        {
            _lastWrites[(node, property)] = value;
        }
    }

    public void Forget(TestNode node, int property)
    {
        lock (_lock)
        {
            _lastWrites.Remove((node, property));
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastWrites.Clear();
        }
    }

    public IReadOnlyList<string> Verify(IReadOnlyCollection<TestNode> reachableNodes)
    {
        var reachable = new HashSet<TestNode>(reachableNodes);
        var violations = new List<string>();

        lock (_lock)
        {
            foreach (var ((node, property), expected) in _lastWrites)
            {
                // A node the run removed structurally carries no durability claim.
                if (!reachable.Contains(node))
                {
                    continue;
                }

                var actual = ReadProperty(node, property);
                if (!Equals(expected, actual))
                {
                    violations.Add($"property {property}: wrote '{expected}', model holds '{actual}'");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// One reader per mutable property on <see cref="TestNode"/>, in the same index order both
    /// mutation strategies assign by. Sized from itself rather than from a repeated count, so the
    /// static constructor below is what actually keeps this in step with
    /// <see cref="ConnectorTesterConfiguration.MutablePropertyCount"/>.
    /// </summary>
    private static readonly Func<TestNode, object?>[] PropertyReaders =
    [
        node => node.StringValue,
        node => node.DecimalValue,
        node => node.IntValue,
        node => node.LongValue
    ];

    static WriteDurabilityLedger()
    {
        if (PropertyReaders.Length != ConnectorTesterConfiguration.MutablePropertyCount)
        {
            throw new InvalidOperationException(
                $"WriteDurabilityLedger has {PropertyReaders.Length} property reader(s) but " +
                $"ConnectorTesterConfiguration.MutablePropertyCount is {ConnectorTesterConfiguration.MutablePropertyCount}. " +
                "Update both together, or the two mutation strategies and this ledger will drift apart.");
        }
    }

    private static object? ReadProperty(TestNode node, int property) =>
        property >= 0 && property < PropertyReaders.Length ? PropertyReaders[property](node) : null;
}
