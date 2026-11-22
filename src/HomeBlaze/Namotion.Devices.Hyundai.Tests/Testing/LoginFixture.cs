using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>Queues the answers of a successful CCI login on a fake handler.</summary>
internal static class LoginFixture
{
    public const string SuccessLocation = "https://oneapp.hyundai.com/redirect?code=CODE-1&state=ccsp";

    /// <param name="handler">The handler that receives the queued answers.</param>
    /// <param name="signInLocation">The <c>Location</c> header of the sign in answer.</param>
    /// <param name="modulusOverride">Replaces the modulus of the RSA key in the certs answer, to test malformed keys.</param>
    /// <param name="tokenGeneration">The generation of the token set the token answer returns.</param>
    /// <returns>The RSA key whose public part the certs answer carries; the caller disposes it.</returns>
    public static RSA Respond(FakeHttpMessageHandler handler, string signInLocation = SuccessLocation, string? modulusOverride = null, int tokenGeneration = 1)
    {
        var key = RSA.Create(2048);
        var parameters = key.ExportParameters(false);
        var modulus = modulusOverride ?? Base64Url.EncodeToString(parameters.Modulus);
        var exponent = Base64Url.EncodeToString(parameters.Exponent);
        var certs = $$$"""{"retValue":{"kid":"key-1","n":"{{{modulus}}}","e":"{{{exponent}}}"}}""";
        handler
            .Respond(BluelinkResponses.AuthorizePath, HttpStatusCode.OK, "<html>login</html>", ("Set-Cookie", "session=abc; Path=/"))
            .Respond(BluelinkResponses.CertsPath, HttpStatusCode.OK, certs)
            .Respond(BluelinkResponses.SignInPath, HttpStatusCode.Found, "", ("Location", signInLocation))
            .Respond(BluelinkResponses.TokenPath, HttpStatusCode.OK, BluelinkResponses.Tokens(tokenGeneration));
        return key;
    }
}
