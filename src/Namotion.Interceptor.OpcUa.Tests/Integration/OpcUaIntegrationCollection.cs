namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Collection for all OPC UA integration tests.
/// Tests use the port pool for isolation and can run in parallel.
/// Timeouts are set generously (60s) to handle resource contention.
/// </summary>
[CollectionDefinition("OPC UA Integration")]
public class OpcUaIntegrationCollection
{
}
