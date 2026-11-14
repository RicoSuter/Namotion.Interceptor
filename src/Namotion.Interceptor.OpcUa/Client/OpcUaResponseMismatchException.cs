using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Reports that an OPC UA call received the response to a different request. Nothing in that response
/// may be applied.
/// </summary>
internal sealed class OpcUaResponseMismatchException : InvalidOperationException
{
    private OpcUaResponseMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Checks that <paramref name="responseHeader"/> answers the request sent with <paramref name="requestHeader"/>,
    /// whose request handle the SDK assigns while sending.
    /// </summary>
    /// <returns>The mismatch, or <c>null</c> when the response answers the request.</returns>
    public static OpcUaResponseMismatchException? Check(string serviceName, RequestHeader requestHeader, ResponseHeader? responseHeader)
    {
        var responseHandle = responseHeader?.RequestHandle ?? 0;

        // A server that does not echo the request handle answers with zero, which proves nothing either way.
        if (responseHandle == 0 || responseHandle == requestHeader.RequestHandle)
        {
            return null;
        }

        return new OpcUaResponseMismatchException(
            $"The OPC UA {serviceName} request {requestHeader.RequestHandle} received the response to a different request ({responseHandle}).");
    }

    /// <summary>
    /// Creates the mismatch for a response of another service type, which the SDK reports as a failed cast.
    /// </summary>
    public static OpcUaResponseMismatchException FromInvalidCast(string serviceName, InvalidCastException exception) =>
        // Not kept as the inner exception: the write retry queue logs this exception again, and the cast's
        // stack adds nothing beyond the type names in its message.
        new($"The OPC UA {serviceName} request received the response to a different request: {exception.Message}");
}
