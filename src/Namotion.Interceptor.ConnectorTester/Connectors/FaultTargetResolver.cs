using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public sealed class FaultTargetResolver : IFaultTargetResolver
{
    private readonly Dictionary<string, IFaultInjectable> _resolved = new(StringComparer.Ordinal);

    public FaultTargetResolver(
        IReadOnlyDictionary<string, TestNode> participants,
        IEnumerable<ISubjectConnector> connectors)
    {
        foreach (var (participantName, root) in participants)
        {
            var connector = connectors.FirstOrDefault(c => c.RootSubject == root);
            if (connector is IFaultInjectable faultInjectable)
            {
                _resolved[participantName] = faultInjectable;
            }
        }
    }

    public IFaultInjectable? Resolve(string participantName)
        => _resolved.TryGetValue(participantName, out var target) ? target : null;
}
