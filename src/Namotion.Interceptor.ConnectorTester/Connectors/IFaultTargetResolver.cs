using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public interface IFaultTargetResolver
{
    IFaultInjectable? Resolve(string participantName);
}
