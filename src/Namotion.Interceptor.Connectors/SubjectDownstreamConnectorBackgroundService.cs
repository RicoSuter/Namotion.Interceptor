using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

public class SubjectDownstreamConnectorBackgroundService : SubjectConnectorBackgroundService
{
    public SubjectDownstreamConnectorBackgroundService(
        ISubjectDownstreamConnector connector,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null)
        : base(connector, context, logger, bufferTime, retryTime)
    {
        UpdateBuffer = new ConnectorUpdateBuffer(connector, null, logger);
    }
}
