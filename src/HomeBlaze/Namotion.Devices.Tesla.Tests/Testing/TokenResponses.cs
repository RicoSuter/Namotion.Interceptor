using System.Text;

namespace Namotion.Devices.Tesla.Tests.Testing;

/// <summary>Responses of the Tesla authorization server.</summary>
internal static class TokenResponses
{
    /// <summary>A token response with an ID token for owner@example.com.</summary>
    public static string TokenJson(string accessToken, string refreshToken, int expiresIn = 28800) =>
        $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","id_token":"{{IdToken("owner@example.com")}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""";

    /// <summary>An unsigned ID token with the email claim.</summary>
    public static string IdToken(string email)
    {
        static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode($$"""{"email":"{{email}}"}""") + ".";
    }
}
