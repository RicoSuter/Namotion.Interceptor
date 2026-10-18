using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki.Client;

/// <summary>
/// Sends requests to the HTTP API of a Nuki bridge. Thread-safe, but the bridge handles one request at a time, so
/// callers serialize their requests.
/// </summary>
internal sealed class NukiBridgeClient
{
    public const int DefaultPort = 8080;

    /// <summary>The largest response accepted from the bridge or the discovery service, in bytes.</summary>
    public const int MaxResponseSize = 1024 * 1024;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(35);

    private readonly HttpClient _httpClient;
    private readonly string _baseAddress;
    private readonly string? _token;
    private readonly NukiTokenMode _tokenMode;

    public NukiBridgeClient(HttpClient httpClient, string hostAddress, string? token, NukiTokenMode tokenMode)
    {
        if (!TryParseHostAddress(hostAddress, out var host, out var port, out var errorMessage))
        {
            throw new ArgumentException(errorMessage, nameof(hostAddress));
        }

        _httpClient = httpClient;
        _baseAddress = "http://" + (host.Contains(':') ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        _token = token;
        _tokenMode = tokenMode;
    }

    /// <summary>
    /// Gets the host, without brackets, and the port (8080 by default) of a host name or IP address with an optional
    /// port, optionally with an http or https scheme and a trailing slash, or a message why the host address is invalid.
    /// </summary>
    public static bool TryParseHostAddress(
        string? hostAddress,
        [NotNullWhen(true)] out string? host,
        out int port,
        [NotNullWhen(false)] out string? errorMessage)
    {
        host = null;
        var address = TrimSchemeAndTrailingSlash(hostAddress.AsSpan().Trim());

        // An IPv6 address carries a port only in brackets.
        var portSeparator = address.LastIndexOf(':');
        if (portSeparator < address.LastIndexOf(']') || (!address.StartsWith('[') && address.Count(':') > 1))
        {
            portSeparator = -1;
        }

        // Rejects a path, query, user name, spaces and malformed brackets, so the token never goes to another host.
        var hostPart = (portSeparator >= 0 ? address[..portSeparator] : address).ToString();
        var hostType = Uri.CheckHostName(hostPart);
        if (hostType == UriHostNameType.Unknown || (hostPart.StartsWith('[') && hostType != UriHostNameType.IPv6))
        {
            port = 0;
            errorMessage = "The host address must be a host name or IP address with an optional port, without a path, " +
                           "query, user name or spaces. Enclose an IPv6 address with a port in brackets.";
            return false;
        }

        port = DefaultPort;
        if (portSeparator >= 0 && !TryParsePort(address[(portSeparator + 1)..], out port))
        {
            errorMessage = "The port of the host address must be a number from 1 to 65535.";
            return false;
        }

        host = hostPart.Trim('[', ']');
        errorMessage = null;
        return true;
    }

    private static ReadOnlySpan<char> TrimSchemeAndTrailingSlash(ReadOnlySpan<char> address)
    {
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0 && (address[..schemeEnd].Equals("http", StringComparison.OrdinalIgnoreCase) ||
                               address[..schemeEnd].Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            address = address[(schemeEnd + "://".Length)..];
        }

        return address.TrimEnd('/');
    }

    private static bool TryParsePort(ReadOnlySpan<char> text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    public Task<NukiInfoResponse> GetInfoAsync(CancellationToken cancellationToken) =>
        GetAsync("info", null, ReadTimeout, NukiJsonContext.Default.NukiInfoResponse, cancellationToken);

    public Task<NukiDeviceResponse[]> GetListAsync(CancellationToken cancellationToken) =>
        GetAsync("list", null, ReadTimeout, NukiJsonContext.Default.NukiDeviceResponseArray, cancellationToken);

    /// <summary>Runs a lock action and waits until the device has finished it.</summary>
    public Task<NukiActionResponse> LockActionAsync(long nukiId, int deviceType, int action, CancellationToken cancellationToken) =>
        GetAsync(
            "lockAction",
            string.Create(CultureInfo.InvariantCulture, $"nukiId={nukiId}&deviceType={deviceType}&action={action}&nowait=0"),
            ActionTimeout,
            NukiJsonContext.Default.NukiActionResponse,
            cancellationToken);

    /// <summary>
    /// Asks the bridge for its token. The bridge answers once its button is pressed, within 30 seconds. Call it on a
    /// client without a token.
    /// </summary>
    public Task<NukiAuthResponse> AuthAsync(CancellationToken cancellationToken) =>
        GetAsync("auth", null, AuthTimeout, NukiJsonContext.Default.NukiAuthResponse, cancellationToken);

    private async Task<T> GetAsync<T>(
        string path, string? query, TimeSpan timeout, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await _httpClient.GetAsync(BuildAddress(path, query), HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateException(path, response.StatusCode);
            }

            await response.Content.LoadIntoBufferAsync(MaxResponseSize, timeoutSource.Token);
            return await response.Content.ReadFromJsonAsync(typeInfo, timeoutSource.Token)
                ?? throw new InvalidDataException($"The bridge answered /{path} without content.");
        }
        // A request that the disposal of the HTTP client cancels, on a reconnect or a stop, did not time out.
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The bridge did not answer /{path} within {timeout.TotalSeconds:0} seconds.", exception);
        }
    }

    private string BuildAddress(string path, string? query)
    {
        var builder = new StringBuilder(_baseAddress, 256).Append(path);
        if (query is not null)
        {
            builder.Append('?').Append(query);
        }

        if (_token is null)
        {
            return builder.ToString();
        }

        builder.Append(query is null ? '?' : '&');
        if (_tokenMode == NukiTokenMode.Encrypted)
        {
            AppendEncryptedToken(builder, _token);
        }
        else
        {
            builder.Append("token=").Append(Uri.EscapeDataString(_token));
        }

        return builder.ToString();
    }

    // Not cached: the key must not outlive the request.
    private static void AppendEncryptedToken(StringBuilder builder, string token)
    {
        var plaintext = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'},{RandomNumberGenerator.GetInt32(1, 65536)}"));
        Span<byte> nonce = stackalloc byte[SecretBox.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        Span<byte> secretKey = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            SHA256.HashData(tokenBytes, secretKey);
            var ctoken = SecretBox.Seal(plaintext, nonce, secretKey);

            builder.Append("ctoken=").Append(Convert.ToHexStringLower(ctoken))
                .Append("&nonce=").Append(Convert.ToHexStringLower(nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(secretKey);
        }
    }

    private static HttpRequestException CreateException(string path, HttpStatusCode statusCode) => new(statusCode switch
    {
        HttpStatusCode.Unauthorized =>
            $"/{path}: Token rejected. Check the token, the bridge clock (encrypted tokens are valid for 60 seconds) " +
            "and the token mode (software bridges only accept Plain).",
        HttpStatusCode.Forbidden => $"/{path}: The HTTP API of the bridge is disabled. Enable it in the Nuki app.",
        HttpStatusCode.NotFound => $"/{path}: The bridge does not know the device or the endpoint.",
        HttpStatusCode.ServiceUnavailable => $"/{path}: The bridge is busy or the device is offline.",
        _ => string.Create(CultureInfo.InvariantCulture, $"/{path}: the bridge answered with {(int)statusCode} {statusCode}.")
    }, inner: null, statusCode);
}
