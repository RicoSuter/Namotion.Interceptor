using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;

internal interface IReadAfterWriteRegistrar
{
    void RegisterProperty(NodeId nodeId, RegisteredSubjectProperty property, int? requestedSamplingInterval, TimeSpan revisedSamplingInterval);
}
