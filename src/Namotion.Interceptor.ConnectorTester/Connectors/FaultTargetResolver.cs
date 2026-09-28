using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public sealed class FaultTargetResolver : IFaultTargetResolver
{
    // volatile guarantees that the Bind-time publication of the new dictionary becomes visible
    // to ChaosEngine threads calling Resolve without an explicit memory barrier. Bind is called
    // once by the host between builder.Build() and host.RunAsync(), so the happens-before
    // ordering through Task scheduling already covers the common path; the volatile makes
    // the contract explicit and protects future callers that might rebind.
    private volatile Dictionary<string, IFaultInjectable> _resolved = new(StringComparer.Ordinal);

    public void Bind(
        IReadOnlyDictionary<string, TestNode> participants,
        IEnumerable<ISubjectConnector> connectors)
    {
        var next = new Dictionary<string, IFaultInjectable>(StringComparer.Ordinal);
        foreach (var (participantName, root) in participants)
        {
            var connector = connectors.FirstOrDefault(c => c.RootSubject == root);
            if (connector is IFaultInjectable faultInjectable)
            {
                next[participantName] = faultInjectable;
            }
        }
        _resolved = next;
    }

    public IFaultInjectable? Resolve(string participantName)
        => _resolved.TryGetValue(participantName, out var target) ? target : null;
}
