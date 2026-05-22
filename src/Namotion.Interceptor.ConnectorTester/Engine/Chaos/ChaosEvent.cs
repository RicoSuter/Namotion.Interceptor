using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Engine.Chaos;

public record ChaosEvent(FaultType FaultType, DateTimeOffset DisruptedAt, DateTimeOffset RecoveredAt)
{
    public TimeSpan Duration => RecoveredAt - DisruptedAt;
}
