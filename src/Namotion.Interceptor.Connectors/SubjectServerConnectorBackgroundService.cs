using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

public class SubjectServerConnectorBackgroundService : SubjectConnectorBackgroundService
{
    public SubjectServerConnectorBackgroundService(
        ISubjectServerConnector connector,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null)
        : base(connector, context, logger, bufferTime, retryTime)
    {
        PropertyWriter = new SubjectPropertyWriter(connector, null, logger);
    }
}
