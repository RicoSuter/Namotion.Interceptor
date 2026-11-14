using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Forwards an incoming request and completes it at most once: the first of <see cref="CallAsync"/> and
/// <see cref="OperationCompleted"/> claims the request, and every later call is a no-op.
/// </summary>
internal sealed class SingleCompletionIncomingRequest : IEndpointIncomingRequest
{
    private readonly IEndpointIncomingRequest _request;
    private int _isClaimed;

    public SingleCompletionIncomingRequest(IEndpointIncomingRequest request)
    {
        _request = request;
    }

    /// <inheritdoc />
    public IServiceRequest Request => _request.Request;

    /// <inheritdoc />
    public SecureChannelContext SecureChannelContext => _request.SecureChannelContext;

    /// <inheritdoc />
    public ValueTask CallAsync(CancellationToken cancellationToken = default)
    {
        // Claimed on entry, not when the response is set: the forwarded call sets the response itself once
        // the service returns, so it cannot be dropped later, and a completion arriving meanwhile must be.
        // This relies on the SDK's CallAsync completing the request on every path, success and failure.
        // For the same reason a failure of the forwarded call must not complete the request again here.
        return TryClaim() ? _request.CallAsync(cancellationToken) : default;
    }

    /// <inheritdoc />
    public void OperationCompleted(IServiceResponse? response, ServiceResult error)
    {
        if (TryClaim())
        {
            _request.OperationCompleted(response, error);
        }
    }

    private bool TryClaim() => Interlocked.Exchange(ref _isClaimed, 1) == 0;
}
