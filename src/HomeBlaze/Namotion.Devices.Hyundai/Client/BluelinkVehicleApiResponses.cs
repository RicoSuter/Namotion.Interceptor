using System.Net;
using System.Text.Json;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>Reads the answers of the vehicle API.</summary>
internal static class BluelinkVehicleApiResponses
{
    private const decimal DefaultControlTokenSeconds = 600m;
    private const decimal MaximumControlTokenSeconds = 86400m;

    /// <summary>Reads the answer of the PIN exchange.</summary>
    public static BluelinkCcsToken ParseControlToken(HttpStatusCode statusCode, string text, DateTimeOffset now)
    {
        const string noTokenMessage = "The PIN exchange returned no control token.";
        using var document = Parse(statusCode, text, noTokenMessage);
        var root = document.RootElement;
        if (root.GetString("controlToken") is { Length: > 0 } controlToken)
        {
            var seconds = root.GetDecimal("expiresTime") is { } expiresTime and >= 1m and <= MaximumControlTokenSeconds
                ? expiresTime
                : DefaultControlTokenSeconds;
            return new BluelinkCcsToken(controlToken, now + TimeSpan.FromSeconds((double)seconds));
        }

        ThrowIfFailed(statusCode, root, isHttpFailure: false);
        throw (int)statusCode is >= 400 and <= 499
            ? new BluelinkPinRejectedException(statusCode, root.GetString("errCode") ?? root.GetString("resCode"))
            : new BluelinkApiException(statusCode, null, noTokenMessage);
    }

    /// <summary>Checks the <c>retCode</c>/<c>resCode</c> envelope of the vehicle API and returns its root.</summary>
    public static JsonElement ParseEnvelope(HttpStatusCode statusCode, string text)
    {
        using var document = Parse(statusCode, text, "The response is not JSON.");
        var root = document.RootElement;
        ThrowIfFailed(statusCode, root, isHttpFailure: (int)statusCode is < 200 or > 299);
        return root.Clone();
    }

    /// <summary>Throws for an <c>error</c> answer, a <c>retCode</c> of <c>F</c>, or any answer when <paramref name="isHttpFailure"/> is set.</summary>
    private static void ThrowIfFailed(HttpStatusCode statusCode, JsonElement root, bool isHttpFailure)
    {
        if (root.GetString("error") is { } error)
        {
            throw new BluelinkApiException(statusCode, null, error);
        }

        if (isHttpFailure || root.GetString("retCode") == "F")
        {
            // resMsg holds the result object on success, so only a string is a message.
            throw new BluelinkApiException(statusCode, root.GetString("resCode"), root.Find("resMsg") is { ValueKind: JsonValueKind.String } resMsg ? resMsg.GetString() : null);
        }
    }

    private static JsonDocument Parse(HttpStatusCode statusCode, string text, string notJsonMessage)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new BluelinkApiException(statusCode, null, notJsonMessage);
        }
    }
}
