using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkAuthClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    private static (FakeHttpMessageHandler Handler, RSA Key) CreateLoginHandler(string signInLocation, string? modulusOverride = null)
    {
        var key = RSA.Create(2048);
        var parameters = key.ExportParameters(false);
        var modulus = modulusOverride ?? Base64Url.EncodeToString(parameters.Modulus);
        var exponent = Base64Url.EncodeToString(parameters.Exponent);
        var certs = $$$"""{"retValue":{"kid":"key-1","n":"{{{modulus}}}","e":"{{{exponent}}}"}}""";
        var handler = new FakeHttpMessageHandler()
            .Respond(BluelinkResponses.AuthorizePath, HttpStatusCode.OK, "<html>login</html>", ("Set-Cookie", "session=abc; Path=/"))
            .Respond(BluelinkResponses.CertsPath, HttpStatusCode.OK, certs)
            .Respond(BluelinkResponses.SignInPath, HttpStatusCode.Found, "", ("Location", signInLocation))
            .Respond(BluelinkResponses.TokenPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1));
        return (handler, key);
    }

    private static BluelinkAuthClient CreateClient(FakeHttpMessageHandler handler) => new(new HttpClient(handler), new FakeTimeProvider(Now));

    [Fact]
    public async Task WhenLoginSucceeds_ThenTokensAreReturnedAndThePasswordIsEncrypted()
    {
        // Arrange
        var (handler, key) = CreateLoginHandler("https://oneapp.hyundai.com/redirect?code=CODE-1&state=ccsp");
        using var _ = key;

        // Act
        var result = await CreateClient(handler).LoginAsync("user@example.invalid", "password-1", "device-1", CancellationToken.None);

        // Assert
        Assert.Equal(BluelinkResponses.TokenSet(1), result.Tokens);
        Assert.Equal(TimeSpan.FromSeconds(3599), result.ExpiresIn);

        var signIn = handler.Requests.Single(request => request.Uri.AbsolutePath == BluelinkResponses.SignInPath);
        Assert.Equal("session=abc", signIn.Header("Cookie"));
        var form = signIn.Body!.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal("user@example.invalid", form["username"]);
        Assert.Equal("true", form["encryptedPassword"]);
        Assert.Equal("key-1", form["kid"]);
        Assert.Equal("password-1", Encoding.UTF8.GetString(key.Decrypt(Convert.FromHexString(form["password"]), RSAEncryptionPadding.Pkcs1)));

        var token = handler.Requests.Single(request => request.Uri.AbsolutePath == BluelinkResponses.TokenPath);
        Assert.Equal("?code=CODE-1", token.Uri.Query);
        Assert.Equal(BluelinkFingerprint.Compute(token.Header("X-Timestamp")!, string.Empty), token.Header("X-Fingerprint"));
    }

    [Fact]
    public async Task WhenSignInReturnsToAuthorize_ThenReauthenticationIsRequired()
    {
        // Arrange
        var (handler, key) = CreateLoginHandler("https://idpconnect-eu.hyundai.com/auth/api/v2/user/oauth2/authorize?state=ccsp");
        using var _ = key;

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkReauthenticationRequiredException>(() =>
            CreateClient(handler).LoginAsync("user@example.invalid", "wrong", "device-1", CancellationToken.None));

        // Assert
        Assert.Contains("email or password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenSignInAsksForConsent_ThenMessageAsksToAcceptTheTerms()
    {
        // Arrange
        var (handler, key) = CreateLoginHandler("https://idpconnect-eu.hyundai.com/web/v1/user/authorization?state=ccsp");
        using var _ = key;

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkReauthenticationRequiredException>(() =>
            CreateClient(handler).LoginAsync("user@example.invalid", "password-1", "device-1", CancellationToken.None));

        // Assert
        Assert.Equal("Accept the new terms once in the Bluelink app.", exception.Message);
    }

    [Fact]
    public async Task WhenTheRsaKeyIsMalformed_ThenLoginThrowsApiExceptionWithoutSigningIn()
    {
        // Arrange
        var (handler, key) = CreateLoginHandler("https://oneapp.hyundai.com/redirect?code=CODE-1&state=ccsp", modulusOverride: "!!!");
        using var _ = key;

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() =>
            CreateClient(handler).LoginAsync("user@example.invalid", "password-1", "device-1", CancellationToken.None));

        // Assert
        Assert.Equal("Bluelink returned 200: The login returned no usable RSA key.", exception.Message);
        Assert.Equal(0, handler.Count(BluelinkResponses.SignInPath));
    }

    [Fact]
    public async Task WhenTheRsaKeyIsTooSmallForThePassword_ThenLoginThrowsApiException()
    {
        // Arrange
        var (handler, key) = CreateLoginHandler("https://oneapp.hyundai.com/redirect?code=CODE-1&state=ccsp", modulusOverride: "AQAB");
        using var _ = key;

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() =>
            CreateClient(handler).LoginAsync("user@example.invalid", "password-1", "device-1", CancellationToken.None));

        // Assert
        Assert.Equal(0, handler.Count(BluelinkResponses.SignInPath));
        Assert.Contains("RSA key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenRefreshSucceeds_ThenBodyIsSignedAndMissingTokensKeepTheirValue()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK,
            """{"accessToken":"cci-access-2","refreshToken":"refresh-2","expiresIn":3599}""", ("Set-Cookie", "t=exchangeable-2; Path=/"));

        // Act
        var result = await CreateClient(handler).RefreshAsync(BluelinkResponses.TokenSet(1), "device-1", CancellationToken.None);

        // Assert
        Assert.Equal("refresh-2", result.Tokens.RefreshToken);
        Assert.Equal("cci-access-2", result.Tokens.AccessToken);
        Assert.Equal("exchangeable-2", result.Tokens.ExchangeableAccessToken);
        Assert.Equal("non-ccs-1", result.Tokens.NonCcsToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(BluelinkResponses.TokenSet(1).Serialize(), request.Body);
        Assert.Equal(BluelinkFingerprint.Compute(request.Header("X-Timestamp")!, request.Body!), request.Header("X-Fingerprint"));
    }

    [Fact]
    public async Task WhenRefreshIsRejectedByTheFingerprintGate_ThenExceptionSaysSo()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(9036));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() =>
            CreateClient(handler).RefreshAsync(BluelinkResponses.TokenSet(1), "device-1", CancellationToken.None));

        // Assert
        Assert.True(exception.IsFingerprintRejected);
    }

    [Fact]
    public async Task WhenCcsTokenIsExchanged_ThenTokenHeadersAreSent()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));

        // Act
        var token = await CreateClient(handler).ExchangeCcsAsync(BluelinkResponses.TokenSet(1), "device-1", CancellationToken.None);

        // Assert
        Assert.Equal(new BluelinkCcsToken("ccs-1", Now.AddHours(1)), token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("?serviceType=CCS", request.Uri.Query);
        Assert.Equal("Bearer cci-access-1", request.Header("Authorization"));
        Assert.Equal("non-ccs-1", request.Header("Authentication"));
        Assert.Equal("exchangeable-1", request.Header("exchangeable-token"));
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData(0.0, 3600)]
    [InlineData(-5.0, 3600)]
    [InlineData(86400.0, 86400)]
    [InlineData(2592000.0, 2592000)]
    [InlineData(2592001.0, 3600)]
    [InlineData(31536000.0, 3600)]
    [InlineData(1700007200.0, 7200)]
    [InlineData(1702592000.0, 2592000)]
    [InlineData(1702592001.0, 3600)]
    [InlineData(99999999999999999.0, 3600)]
    public void WhenExpiresTimeIsReadDefensively_ThenLifetimeIsPlausible(double? expiresTime, int expectedSeconds)
    {
        // Act
        var expiresAt = BluelinkAuthClient.GetExpiry((decimal?)expiresTime, Now);

        // Assert
        Assert.Equal(Now.AddSeconds(expectedSeconds), expiresAt);
    }

    [Fact]
    public void WhenExpiresTimeIsTheLargestDecimal_ThenLifetimeIsTheDefault()
    {
        // Act
        var expiresAt = BluelinkAuthClient.GetExpiry(decimal.MaxValue, Now);

        // Assert
        Assert.Equal(Now.AddHours(1), expiresAt);
    }

    [Fact]
    public void WhenExpiresTimeIsAPastTimestamp_ThenItIsAlreadyExpired()
    {
        // Act
        var expiresAt = BluelinkAuthClient.GetExpiry(1600000000m, Now);

        // Assert
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), expiresAt);
    }
}
