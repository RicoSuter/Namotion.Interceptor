using System.Collections.Generic;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Error message.
/// </summary>
public class ErrorPayload
{
    /// <summary>
    /// Primary error code.
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; set; }

    /// <summary>
    /// Individual property failures (if applicable).
    /// </summary>
    public IReadOnlyList<PropertyFailure>? Failures { get; set; }
}

/// <summary>
/// Standard error codes for the WebSocket protocol.
/// </summary>
public static class ErrorCode
{
    public const int UnknownProperty = 100;
    public const int ReadOnlyProperty = 101;
    public const int ValidationFailed = 102;
    public const int InvalidFormat = 200;
    public const int VersionMismatch = 201;
    public const int InternalError = 500;
}
