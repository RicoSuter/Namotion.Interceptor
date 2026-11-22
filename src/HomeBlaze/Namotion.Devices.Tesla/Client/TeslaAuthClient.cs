using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Namotion.Devices.Tesla.Client.Dto;

namespace Namotion.Devices.Tesla.Client;

/// <summary>A token set from the Tesla authorization server. <see cref="object.ToString"/> leaves out the tokens.</summary>
internal sealed record TeslaTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? Email)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ExpiresAt = {ExpiresAt:O}, Email = {Email}");
        return true;
    }
}

/// <summary>
/// A started browser login: open <see cref="Url"/>, then exchange the code of the pasted callback with <see cref="CodeVerifier"/>.
/// <see cref="object.ToString"/> leaves out the code verifier.
/// </summary>
public sealed record TeslaLogin(string Url, string State, string CodeVerifier)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Url = {Url}, State = {State}");
        return true;
    }
}

/// <summary>
/// Calls the Tesla authorization server for the Owner API client.
/// </summary>
internal sealed class TeslaAuthClient
{
    private const string TokenAddress = "https://auth.tesla.com/oauth2/v3/token";
    private const string AuthorizeAddress = "https://auth.tesla.com/oauth2/v3/authorize";
    private const string ClientId = "ownerapi";
    private const string RedirectAddress = "tesla://auth/callback";
    private const string Scope = "openid email offline_access";
    private const int DefaultExpiresInSeconds = 28800;

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">A client that uses HTTP/2 and TLS 1.3, which Tesla requires.</param>
    public TeslaAuthClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>Exchanges a refresh token for new tokens. The returned refresh token replaces the given one.</summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token.</exception>
    /// <exception cref="TeslaApiException">The authorization server failed.</exception>
    public Task<TeslaTokens> RefreshAsync(string refreshToken, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        }, timeProvider, cancellationToken);

    /// <summary>Exchanges the code of a browser login for tokens.</summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the code.</exception>
    /// <exception cref="TeslaApiException">The authorization server failed.</exception>
    public Task<TeslaTokens> ExchangeCodeAsync(string code, string codeVerifier, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = RedirectAddress
        }, timeProvider, cancellationToken);

    /// <summary>Starts a browser login with PKCE (S256) and a random state.</summary>
    public static TeslaLogin CreateLogin()
    {
        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var url = AuthorizeAddress +
            "?client_id=" + ClientId +
            "&redirect_uri=" + Uri.EscapeDataString(RedirectAddress) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString(Scope) +
            "&code_challenge=" + challenge +
            "&code_challenge_method=S256" +
            "&state=" + state;
        return new TeslaLogin(url, state, codeVerifier);
    }

    /// <summary>
    /// Extracts the code from a pasted callback URL or query string and checks its state.
    /// </summary>
    /// <exception cref="FormatException">The text holds an error from Tesla, has no code or belongs to another login.</exception>
    public static string ParseCallbackCode(string pasted, string expectedState)
    {
        var query = pasted.Trim();
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
        {
            query = query[..fragmentStart];
        }

        var queryStart = query.IndexOf('?');
        if (queryStart >= 0)
        {
            query = query[(queryStart + 1)..];
        }

        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (ReadQueryParameter(parameters, "error") is { Length: > 0 } error)
        {
            var description = ReadQueryParameter(parameters, "error_description");
            throw new FormatException(
                $"Tesla refused the sign in: {error}{(string.IsNullOrEmpty(description) ? "" : $" ({description})")}. Start the sign in again.");
        }

        var code = ReadQueryParameter(parameters, "code");
        if (string.IsNullOrEmpty(code))
        {
            throw new FormatException("The pasted text contains no code. Copy the whole tesla://auth/callback?code=... address.");
        }

        if (ReadQueryParameter(parameters, "state") != expectedState)
        {
            throw new FormatException("The pasted address belongs to another login. Start the sign in again.");
        }

        return code;
    }

    private static string? ReadQueryParameter(string[] parameters, string name)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Length > name.Length && parameter[name.Length] == '=' && parameter.StartsWith(name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parameter[(name.Length + 1)..]);
            }
        }

        return null;
    }

    private async Task<TeslaTokens> RequestTokensAsync(Dictionary<string, string> form, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var request = _httpClient.CreateRequest(HttpMethod.Post, TokenAddress);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var dto = TryDeserialize(json) ?? new TeslaTokenResponseDto();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new TeslaApiException(response.StatusCode, dto.Error, TeslaHttp.GetRetryAfter(response, timeProvider));
        }

        EnsureTokens(response.StatusCode, dto);

        return new TeslaTokens(
            dto.AccessToken!,
            dto.RefreshToken!,
            timeProvider.GetUtcNow().AddSeconds(dto.ExpiresIn is int expiresIn and > 0 ? expiresIn : DefaultExpiresInSeconds),
            ReadEmail(dto.IdToken));
    }

    private static void EnsureTokens(HttpStatusCode statusCode, TeslaTokenResponseDto dto)
    {
        if (statusCode == HttpStatusCode.Unauthorized || dto.Error is "login_required" or "invalid_grant")
        {
            throw new TeslaReauthenticationRequiredException(dto.Error);
        }

        if ((int)statusCode is < 200 or > 299 || dto.AccessToken is null || dto.RefreshToken is null)
        {
            // Only the error code: the body of a token response holds tokens.
            throw new TeslaApiException(statusCode, dto.Error ?? "Invalid token response");
        }
    }

    private static TeslaTokenResponseDto? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaTokenResponseDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadEmail(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("email", out var email) &&
                email.ValueKind == JsonValueKind.String
                    ? email.GetString()
                    : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
