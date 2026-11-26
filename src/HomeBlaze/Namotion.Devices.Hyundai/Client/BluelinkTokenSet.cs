using System.Text;
using System.Text.Json;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// The seven tokens of the CCI login. A refresh sends all of them and rotates them; the previous set is invalid
/// afterwards. <see cref="object.ToString"/> leaves out every token.
/// </summary>
internal sealed record BluelinkTokenSet(
    string AccessToken,
    string RefreshToken,
    string ExchangeableAccessToken,
    string ExchangeableRefreshToken,
    string NonCcsToken,
    string NonCcsRefreshToken,
    string IdToken)
{
    /// <summary>Writes the set as the JSON body of <c>/auth/v2/token-refresh</c>, which is also the stored form.</summary>
    public string Serialize() => BluelinkJson.Write(writer =>
    {
        writer.WriteString("accessToken", AccessToken);
        writer.WriteString("refreshToken", RefreshToken);
        writer.WriteString("exchangeableAccessToken", ExchangeableAccessToken);
        writer.WriteString("exchangeableRefreshToken", ExchangeableRefreshToken);
        writer.WriteString("nonCcsToken", NonCcsToken);
        writer.WriteString("nonCcsRefreshToken", NonCcsRefreshToken);
        writer.WriteString("idToken", IdToken);
    });

    /// <exception cref="FormatException">The text is not a serialized token set.</exception>
    public static BluelinkTokenSet Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return FromJson(document.RootElement, previous: null)
                ?? throw new FormatException("The stored Bluelink tokens have no refresh token.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("The stored Bluelink tokens are not valid JSON.", exception);
        }
    }

    /// <summary>Reads a token response; a token missing in the response keeps its previous value.</summary>
    /// <returns>The set, or null when neither the response nor the previous set has a refresh token.</returns>
    public static BluelinkTokenSet? FromJson(JsonElement element, BluelinkTokenSet? previous)
    {
        var refreshToken = Read(element, "refreshToken", previous?.RefreshToken);
        if (refreshToken.Length == 0)
        {
            return null;
        }

        return new BluelinkTokenSet(
            Read(element, "accessToken", previous?.AccessToken),
            refreshToken,
            Read(element, "exchangeableAccessToken", previous?.ExchangeableAccessToken),
            Read(element, "exchangeableRefreshToken", previous?.ExchangeableRefreshToken),
            Read(element, "nonCcsToken", previous?.NonCcsToken),
            Read(element, "nonCcsRefreshToken", previous?.NonCcsRefreshToken),
            Read(element, "idToken", previous?.IdToken));
    }

    private static string Read(JsonElement element, string name, string? fallback) =>
        element.GetString(name) is { Length: > 0 } value ? value : fallback ?? string.Empty;

    public override string ToString() => "BluelinkTokenSet { Tokens = <hidden> }";
}

/// <summary>A token set with the lifetime of its CCI access token.</summary>
internal sealed record BluelinkCciTokens(BluelinkTokenSet Tokens, TimeSpan ExpiresIn);

/// <summary>An access or control token of the vehicle API and when it expires. <see cref="object.ToString"/> leaves out the token.</summary>
internal sealed record BluelinkCcsToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ExpiresAt = ").Append(ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}
