using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Sources;

public class SubjectServerSourceBackgroundService : SubjectSourceBackgroundService
{
    public SubjectServerSourceBackgroundService(
        ISubjectServerSource source,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null) 
        : base(source, context, logger, bufferTime, retryTime)
    {
        UpdateBuffer = new SourceUpdateBuffer(source, null, logger);
    }
}
