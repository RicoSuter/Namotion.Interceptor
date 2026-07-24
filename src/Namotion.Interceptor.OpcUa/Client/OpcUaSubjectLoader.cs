using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.LoadPlan;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaSubjectLoader
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;

    public OpcUaSubjectLoader(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _subject = subject;
        _configuration = configuration;
        _ownership = ownership;
        _source = source;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        var planner = new OpcUaLoadPlanner(_subject, _configuration, _source, _logger);
        var plan = await planner.CreatePlanAsync(subject, node, session, cancellationToken).ConfigureAwait(false);
        return plan.Commit(_source);
    }
}
