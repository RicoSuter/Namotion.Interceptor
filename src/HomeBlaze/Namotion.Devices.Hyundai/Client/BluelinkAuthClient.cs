using System.Net;
using System.Text;
using System.Text.Json;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// The CCI login of the Hyundai OneApp: sign in with the RSA-encrypted password, exchange the code for the CCI token
/// set, refresh it, and exchange it for the CCS access token of the vehicle API. Never logs or throws a token.
/// </summary>
internal sealed class BluelinkAuthClient(HttpClient httpClient, TimeProvider timeProvider)
{
    private const int MaximumRedirects = 10;
    private const int MaximumPasswordLengthHint = 20;
    private const decimal OneYearInSeconds = 31_536_000m;
    private const decimal MaximumLifetimeInSeconds = 2_592_000m;

    private static readonly TimeSpan DefaultCcsLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultCciLifetime = TimeSpan.FromSeconds(3599);

    /// <summary>Signs in with email and password and returns a new token set.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">Hyundai rejected the credentials or asks to accept new terms.</exception>
    /// <exception cref="BluelinkApiException">The login failed otherwise, for example because it changed.</exception>
    public async Task<BluelinkCciTokens> LoginAsync(string email, string password, string deviceId, CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        await AuthorizeAsync(cookies, cancellationToken);
        var (keyId, modulus, exponent) = await GetPublicKeyAsync(cookies, cancellationToken);
        var code = await SignInAsync(email, password, keyId, BluelinkPasswordEncryptor.Encrypt(password, modulus, exponent), cookies, cancellationToken);
        return await ExchangeCodeAsync(code, deviceId, cancellationToken);
    }

    /// <summary>Refreshes the token set. Every refresh rotates all tokens; the given set is invalid afterwards.</summary>
    /// <exception cref="BluelinkApiException">Hyundai rejected the refresh.</exception>
    public async Task<BluelinkCciTokens> RefreshAsync(BluelinkTokenSet tokens, string deviceId, CancellationToken cancellationToken)
    {
        var body = tokens.Serialize();
        using var request = new HttpRequestMessage(HttpMethod.Post, BluelinkAppConstants.CciHost + "/auth/v2/token-refresh")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        BluelinkFingerprint.AddHeaders(request, deviceId, body, timeProvider);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var root = await ReadCciResponseAsync(response, cancellationToken);
        var refreshed = ReadCciTokens(root, tokens);

        // An updated exchangeable token may arrive in the cookie t instead of the body.
        return BluelinkLoginResponses.GetCookie(response, "t") is { Length: > 0 } exchangeableToken
            ? refreshed with { Tokens = refreshed.Tokens with { ExchangeableAccessToken = exchangeableToken } }
            : refreshed;
    }

    /// <summary>Exchanges the CCI access token for the CCS access token of the vehicle API. Does not rotate the token set.</summary>
    /// <exception cref="BluelinkApiException">Hyundai rejected the exchange.</exception>
    public async Task<BluelinkCcsToken> ExchangeCcsAsync(BluelinkTokenSet tokens, string deviceId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BluelinkAppConstants.CciHost + "/auth/v1/token-exchange?serviceType=CCS")
        {
            Content = new ByteArrayContent([])
        };
        BluelinkFingerprint.AddHeaders(request, deviceId, string.Empty, timeProvider);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokens.AccessToken);
        request.Headers.TryAddWithoutValidation("Authentication", tokens.NonCcsToken);
        if (tokens.ExchangeableAccessToken.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("exchangeable-token", tokens.ExchangeableAccessToken);
            request.Headers.TryAddWithoutValidation("non-ccs-token", tokens.NonCcsToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var root = await ReadCciResponseAsync(response, cancellationToken);
        var accessToken = root.GetString("accessToken") ?? root.GetString("ccsAccessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new BluelinkApiException(response.StatusCode, null, "The token exchange returned no access token.");
        }

        return new BluelinkCcsToken(accessToken, GetExpiry(root.GetDecimal("expiresTime"), timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// A value above one year is a Unix timestamp; a missing or non-positive value, a past timestamp, or anything more
    /// than 30 days ahead is one hour. Never throws.
    /// </summary>
    internal static DateTimeOffset GetExpiry(decimal? expiresTime, DateTimeOffset now)
    {
        var defaultExpiry = now + DefaultCcsLifetime;
        if (expiresTime is not > 0m)
        {
            return defaultExpiry;
        }

        var value = expiresTime.Value;
        if (value <= OneYearInSeconds)
        {
            return value <= MaximumLifetimeInSeconds ? now + TimeSpan.FromSeconds((double)value) : defaultExpiry;
        }

        // hyundai_kia_connect_api reads expiresTime as a lifetime in seconds and evcc as a Unix timestamp, so both are accepted.
        var nowSeconds = now.ToUnixTimeSeconds();
        return value > nowSeconds && value <= nowSeconds + MaximumLifetimeInSeconds
            ? DateTimeOffset.FromUnixTimeSeconds((long)value)
            : defaultExpiry;
    }

    private async Task AuthorizeAsync(CookieContainer cookies, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{BluelinkAppConstants.LoginHost}/auth/api/v2/user/oauth2/authorize?response_type=code" +
            $"&client_id={BluelinkAppConstants.OneAppClientId}&redirect_uri={BluelinkAppConstants.OneAppRedirectAddress}" +
            "&lang=en&state=ccsp&country=de");

        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var response = await SendLoginAsync(HttpMethod.Get, uri, null, cookies, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (text.Contains("abusing", StringComparison.OrdinalIgnoreCase) || uri.AbsoluteUri.Contains("/error?status=400", StringComparison.Ordinal))
            {
                throw new BluelinkApiException(response.StatusCode, null, "Hyundai blocked the login as an abusing request; the login changed and the integration needs an update.");
            }

            return;
        }

        throw new BluelinkApiException(HttpStatusCode.Redirect, null, "The login redirected too often.");
    }

    private async Task<(string KeyId, string Modulus, string Exponent)> GetPublicKeyAsync(CookieContainer cookies, CancellationToken cancellationToken)
    {
        using var response = await SendLoginAsync(HttpMethod.Get, new Uri(BluelinkAppConstants.LoginHost + "/auth/api/v1/accounts/certs"), null, cookies, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BluelinkApiException(response.StatusCode, null, "The RSA key of the login could not be read.");
        }

        return BluelinkLoginResponses.ParsePublicKey(text)
            ?? throw new BluelinkApiException(response.StatusCode, null, "The login returned no RSA key.");
    }

    private async Task<string> SignInAsync(string email, string password, string keyId, string encryptedPassword, CookieContainer cookies, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(
        [
            new("client_id", BluelinkAppConstants.OneAppClientId),
            new("encryptedPassword", "true"),
            new("password", encryptedPassword),
            new("redirect_uri", BluelinkAppConstants.OneAppRedirectAddress),
            new("scope", string.Empty),
            new("nonce", string.Empty),
            new("state", "ccsp"),
            new("username", email),
            new("connector_session_key", string.Empty),
            new("kid", keyId),
            new("_csrf", string.Empty)
        ]);

        using var response = await SendLoginAsync(HttpMethod.Post, new Uri(BluelinkAppConstants.LoginHost + "/auth/account/signin"), form, cookies, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Found)
        {
            throw BluelinkLoginResponses.CreateSignInStatusException(response.StatusCode);
        }

        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        var query = BluelinkLoginResponses.ParseQuery(location);
        return query.TryGetValue("code", out var code) && code.Length > 0
            ? code
            : throw BluelinkLoginResponses.CreateSignInException(location, query, password.Length > MaximumPasswordLengthHint);
    }

    private async Task<BluelinkCciTokens> ExchangeCodeAsync(string code, string deviceId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BluelinkAppConstants.CciHost}/auth/v1/token?code={Uri.EscapeDataString(code)}")
        {
            Content = new ByteArrayContent([])
        };
        BluelinkFingerprint.AddHeaders(request, deviceId, string.Empty, timeProvider);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return ReadCciTokens(await ReadCciResponseAsync(response, cancellationToken), null);
    }

    private static BluelinkCciTokens ReadCciTokens(JsonElement root, BluelinkTokenSet? previous)
    {
        var tokens = BluelinkTokenSet.FromJson(root, previous)
            ?? throw new BluelinkApiException(HttpStatusCode.OK, null, "The token response has no refresh token.");
        var expiresIn = root.GetInt32("expiresIn") is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultCciLifetime;
        return new BluelinkCciTokens(tokens, expiresIn);
    }

    /// <summary>Returns the JSON root of a CCI auth response; throws with the <c>code</c> of the body when it failed.</summary>
    private static async Task<JsonElement> ReadCciResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? root = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A failed response may have no JSON body; a successful one must have one.
        }

        if (!response.IsSuccessStatusCode || root is null)
        {
            throw new BluelinkApiException(response.StatusCode, root?.GetString("code"), root?.GetString("message"));
        }

        return root.Value;
    }

    private async Task<HttpResponseMessage> SendLoginAsync(HttpMethod method, Uri uri, HttpContent? content, CookieContainer cookies, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", BluelinkAppConstants.LoginUserAgent);
        var cookieHeader = cookies.GetCookieHeader(uri);
        if (cookieHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var response = await httpClient.SendAsync(request, cancellationToken);
        BluelinkLoginResponses.StoreCookies(cookies, uri, response);
        return response;
    }
}
