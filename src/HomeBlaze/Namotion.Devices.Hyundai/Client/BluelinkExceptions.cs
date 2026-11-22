using System.Net;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>A Bluelink call failed. The message never contains a token, a password or a response body, only a short server message, at most 200 characters.</summary>
public class BluelinkApiException(HttpStatusCode statusCode, string? code, string? message)
    : Exception(CreateMessage(statusCode, code, message))
{
    private const string TokenExpiredMessage = "Key not authorized";
    private const int MaxServerMessageLength = 200;

    private readonly string? _rawMessage = message;

    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The <c>resCode</c> of the vehicle API or the <c>code</c> of the auth API, or null.</summary>
    public string? Code { get; } = code;

    internal bool IsRateLimited => Code == "5091";

    internal bool IsTokenExpired =>
        Code == "7501" ||
        _rawMessage?.Contains(TokenExpiredMessage, StringComparison.OrdinalIgnoreCase) == true;

    internal bool IsDeviceNotRegistered => Code == "4002";

    internal bool IsFingerprintRejected => Code is "9035" or "9036" or "9037";

    private static string CreateMessage(HttpStatusCode statusCode, string? code, string? message) => code switch
    {
        "5091" => "Hyundai's request limit was reached (5091).",
        "5031" or "5153" => $"The vehicle does not support this request ({code}).",
        "4002" => "The device is not registered (4002).",
        "4111" => "The refresh token expired (4111).",
        "9035" or "9036" or "9037" => $"Hyundai rejected the app fingerprint ({code}); the login changed and the integration needs an update.",
        _ => CreateUnknownCodeMessage(statusCode, code, message)
    };

    private static string CreateUnknownCodeMessage(HttpStatusCode statusCode, string? code, string? message)
    {
        if (message is not null && message.Contains(TokenExpiredMessage, StringComparison.OrdinalIgnoreCase))
        {
            return "The access token expired.";
        }

        var codeText = code is null ? "" : $" {code}";
        var messageText = message is null ? "." : $": {Truncate(message)}";
        return $"Bluelink returned {(int)statusCode}{codeText}{messageText}";
    }

    private static string Truncate(string message) =>
        message.Length <= MaxServerMessageLength ? message : message[..MaxServerMessageLength];
}

/// <summary>Hyundai rejected the PIN. Five wrong PINs lock PIN entry for a while.</summary>
public sealed class BluelinkPinRejectedException(HttpStatusCode statusCode, string? code)
    : BluelinkApiException(statusCode, code, "Hyundai rejected the PIN.");

/// <summary>Hyundai rejected the credentials, or asks to accept new terms; the user has to sign in again.</summary>
public sealed class BluelinkReauthenticationRequiredException(string message) : Exception(message);

/// <summary>The credentials were replaced while the previous ones were in use; the request was not sent with the replaced credentials.</summary>
public sealed class BluelinkCredentialsReplacedException(string message) : Exception(message);

/// <summary>The daily request limit of the account is reached; requests resume after local midnight.</summary>
public sealed class BluelinkRequestLimitException() : Exception("Daily request limit reached.");

/// <summary>The result of a command, read from the notification records of the vehicle.</summary>
public enum BluelinkCommandResult
{
    Pending,
    Success,
    Failed,
    NoResponse,
    TimedOut
}

/// <summary>A command did not succeed.</summary>
public sealed class BluelinkCommandException(BluelinkCommandResult result) : Exception(CreateMessage(result))
{
    public BluelinkCommandResult Result { get; } = result;

    private static string CreateMessage(BluelinkCommandResult result) => result switch
    {
        BluelinkCommandResult.Failed => "The vehicle rejected the command.",
        BluelinkCommandResult.NoResponse => "The vehicle did not respond to the command.",
        BluelinkCommandResult.TimedOut => "The result of the command did not arrive in time.",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Only a failed, unanswered or timed out command is an exception.")
    };
}
