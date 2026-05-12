using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public sealed class FaultTargetResolver : IFaultTargetResolver
{
    // Populated by the host AFTER builder.Build() returns, before host.RunAsync() starts.
    // Constructing this in DI from IEnumerable<IHostedService> would re-enter the IHostedService
    // factories that depend on IFaultTargetResolver and form a resolution cycle.
    private Dictionary<string, IFaultInjectable> _resolved = new(StringComparer.Ordinal);

    public FaultTargetResolver()
    {
    }

    public FaultTargetResolver(
        IReadOnlyDictionary<string, TestNode> participants,
        IEnumerable<ISubjectConnector> connectors)
    {
        Bind(participants, connectors);
    }

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
