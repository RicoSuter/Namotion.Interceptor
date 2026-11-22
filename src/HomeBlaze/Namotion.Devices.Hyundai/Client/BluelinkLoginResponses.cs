using System.Net;
using System.Text.Json;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>Reads the answers of the login: the public key, the cookies, the query of the sign in redirect and the exception of a failed sign in.</summary>
internal static class BluelinkLoginResponses
{
    /// <summary>Reads the RSA key of the certs response, or null when it has none.</summary>
    public static (string KeyId, string Modulus, string Exponent)? ParsePublicKey(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.Find("retValue") is { } key && key.GetString("n") is { Length: > 0 } modulus && key.GetString("e") is { Length: > 0 } exponent)
            {
                return (key.GetString("kid") ?? string.Empty, modulus, exponent);
            }
        }
        catch (JsonException)
        {
            // Reported as a missing key by the caller.
        }

        return null;
    }

    /// <summary>Reads the query of a redirect address; the first value of a repeated name wins.</summary>
    public static Dictionary<string, string> ParseQuery(string location)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = location.IndexOf('?');
        if (start < 0)
        {
            return query;
        }

        foreach (var pair in location[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            query.TryAdd(name, value);
        }

        return query;
    }

    /// <summary>Stores the <c>Set-Cookie</c> headers of a response in the container of a login.</summary>
    public static void StoreCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            try
            {
                cookies.SetCookies(uri, value);
            }
            catch (CookieException)
            {
                // A malformed cookie is skipped, as a browser does.
            }
        }
    }

    /// <summary>Reads the value of a cookie from the <c>Set-Cookie</c> headers of a response, or null.</summary>
    public static string? GetCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator > 0 && pair.AsSpan(0, separator).Trim().SequenceEqual(name))
            {
                return pair[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Maps a sign in redirect without a code to the exception that tells the user what to do.</summary>
    public static Exception CreateSignInException(string location, Dictionary<string, string> query, bool isLongPassword)
    {
        if (query.TryGetValue("error", out var error) && error.Length > 0)
        {
            var description = query.GetValueOrDefault("error_description", string.Empty).Trim();
            if (description.Length == 0)
            {
                description = "unknown";
            }

            return string.Equals(description, "invalid credentials", StringComparison.OrdinalIgnoreCase)
                ? CreateInvalidCredentialsException(isLongPassword)
                : new BluelinkReauthenticationRequiredException($"Hyundai rejected the sign in: {BluelinkApiException.Truncate(description)}.");
        }

        if (location.Contains("/web/v1/user/authorization", StringComparison.Ordinal))
        {
            return new BluelinkReauthenticationRequiredException("Accept the new terms once in the Bluelink app.");
        }

        return location.Contains("authorize", StringComparison.Ordinal)
            ? CreateInvalidCredentialsException(isLongPassword)
            : new BluelinkApiException(HttpStatusCode.Found, null, "The sign in redirected to an unexpected address.");
    }

    /// <summary>Maps a sign in answer that is not a redirect: a 4xx answer is a rejection and is never retried, so that a wrong password does not lock the account.</summary>
    public static Exception CreateSignInStatusException(HttpStatusCode statusCode) => (int)statusCode is >= 400 and < 500
        ? new BluelinkReauthenticationRequiredException("Hyundai rejected the sign in.")
        : new BluelinkApiException(statusCode, null, "The sign in failed.");

    private static BluelinkReauthenticationRequiredException CreateInvalidCredentialsException(bool isLongPassword) =>
        new(isLongPassword
            ? "Hyundai rejected the email or password. Some accounts accept only the first 20 characters of a longer password."
            : "Hyundai rejected the email or password.");
}
