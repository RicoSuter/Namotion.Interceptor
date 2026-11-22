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
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new BluelinkApiException(statusCode, null, noTokenMessage);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.GetString("controlToken") is { Length: > 0 } controlToken)
            {
                var seconds = root.GetDecimal("expiresTime") is { } expiresTime and >= 1m and <= MaximumControlTokenSeconds
                    ? expiresTime
                    : DefaultControlTokenSeconds;
                return new BluelinkCcsToken(controlToken, now + TimeSpan.FromSeconds((double)seconds));
            }

            if (root.GetString("error") is { } error)
            {
                throw new BluelinkApiException(statusCode, null, error);
            }

            if (root.GetString("retCode") == "F")
            {
                throw new BluelinkApiException(statusCode, root.GetString("resCode"), root.GetString("resMsg"));
            }

            throw (int)statusCode is >= 400 and <= 499
                ? new BluelinkPinRejectedException(statusCode, root.GetString("errCode") ?? root.GetString("resCode"))
                : new BluelinkApiException(statusCode, null, noTokenMessage);
        }
    }

    /// <summary>Checks the <c>retCode</c>/<c>resCode</c> envelope of the vehicle API and returns its root.</summary>
    public static JsonElement ParseEnvelope(HttpStatusCode statusCode, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new BluelinkApiException(statusCode, null, "The response is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.GetString("error") is { } error)
            {
                throw new BluelinkApiException(statusCode, null, error);
            }

            if (root.GetString("retCode") == "F" || (int)statusCode is < 200 or > 299)
            {
                throw new BluelinkApiException(statusCode, root.GetString("resCode"), root.Find("resMsg") is { ValueKind: JsonValueKind.String } resMsg ? resMsg.GetString() : null);
            }

            return root.Clone();
        }
    }
}
