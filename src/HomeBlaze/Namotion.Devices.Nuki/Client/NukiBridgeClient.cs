using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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

    /// <summary>The default delay before a request that the bridge answered with 503 is sent again.</summary>
    public static readonly TimeSpan DefaultBusyRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The largest response accepted from the bridge or the discovery service, in bytes.</summary>
    public const int MaxResponseSize = 1024 * 1024;

    private const int InvalidBrackets = -2;

    private static readonly SearchValues<char> InvalidHostAddressCharacters = SearchValues.Create("/?#@\\");

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(35);

    private readonly HttpClient _httpClient;
    private readonly string _baseAddress;
    private readonly string? _token;
    private readonly NukiTokenMode _tokenMode;
    private readonly TimeSpan _busyRetryDelay;

    public NukiBridgeClient(HttpClient httpClient, string hostAddress, string? token, NukiTokenMode tokenMode, TimeSpan? busyRetryDelay = null)
    {
        _httpClient = httpClient;
        _baseAddress = "http://" + NormalizeHostAddress(hostAddress) + "/";
        _token = token;
        _tokenMode = tokenMode;
        _busyRetryDelay = busyRetryDelay ?? DefaultBusyRetryDelay;
    }

    /// <summary>
    /// Gets the host and port of a bridge without scheme and trailing slash, with the default port when none is given.
    /// An IPv6 address is enclosed in brackets.
    /// </summary>
    /// <exception cref="ArgumentException">The host address is not a host with an optional port.</exception>
    public static string NormalizeHostAddress(string hostAddress) =>
        TryNormalizeHostAddress(hostAddress, out var normalizedHostAddress, out var errorMessage)
            ? normalizedHostAddress
            : throw new ArgumentException(errorMessage, nameof(hostAddress));

    /// <summary>
    /// Gets the host and port of a bridge as <see cref="NormalizeHostAddress"/> does, or a message that describes why
    /// the host address is invalid.
    /// </summary>
    public static bool TryNormalizeHostAddress(
        string? hostAddress,
        [NotNullWhen(true)] out string? normalizedHostAddress,
        [NotNullWhen(false)] out string? errorMessage)
    {
        normalizedHostAddress = null;
        var address = TrimSchemeAndTrailingSlash(hostAddress.AsSpan().Trim());
        if (ContainsInvalidCharacter(address))
        {
            errorMessage = "The host address must be a host name or IP address with an optional port, without a path, query, user name or spaces.";
            return false;
        }

        errorMessage = SplitHostAndPort(address, out var host, out var port);
        if (errorMessage is not null)
        {
            return false;
        }

        normalizedHostAddress = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
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

    private static bool ContainsInvalidCharacter(ReadOnlySpan<char> address)
    {
        foreach (var character in address)
        {
            if (char.IsWhiteSpace(character) || InvalidHostAddressCharacters.Contains(character))
            {
                return true;
            }
        }

        return false;
    }

    // Returns null and the host with the port (the default port when none is given), or a message why it is invalid.
    private static string? SplitHostAndPort(ReadOnlySpan<char> address, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultPort;
        if (!address.StartsWith('[') && address.Count(':') > 1)
        {
            // An IPv6 address without brackets, which cannot carry a port.
            return TrySetUnbracketedIPv6Host(address, out host);
        }

        var portSeparator = FindPortSeparator(address);
        if (portSeparator == InvalidBrackets)
        {
            return "An IPv6 address must be enclosed in brackets, optionally followed by a colon and the port.";
        }

        var hostPart = portSeparator >= 0 ? address[..portSeparator] : address;
        var hostPartError = ValidateHostPart(hostPart);
        if (hostPartError is not null)
        {
            return hostPartError;
        }

        if (portSeparator >= 0 && !TryParsePort(address[(portSeparator + 1)..], out port))
        {
            return "The port of the host address must be a number from 1 to 65535.";
        }

        host = hostPart.ToString();
        return null;
    }

    private static string? ValidateHostPart(ReadOnlySpan<char> hostPart)
    {
        if (hostPart.IsEmpty || hostPart.SequenceEqual("[]"))
        {
            return "The host address must contain a host name or IP address.";
        }

        if (hostPart.StartsWith('[') && !IsIPv6Address(hostPart[1..^1]))
        {
            return "The host address must be a valid IPv6 address when it is enclosed in brackets or contains more than one colon.";
        }

        return null;
    }

    private static string? TrySetUnbracketedIPv6Host(ReadOnlySpan<char> address, out string host)
    {
        if (!IsIPv6Address(address))
        {
            host = string.Empty;
            return "The host address must be a valid IPv6 address when it is enclosed in brackets or contains more than one colon.";
        }

        host = string.Concat("[", address, "]");
        return null;
    }

    private static bool IsIPv6Address(ReadOnlySpan<char> text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    // Gets the index of the colon before the port, -1 when there is no port, or InvalidBrackets.
    private static int FindPortSeparator(ReadOnlySpan<char> address)
    {
        if (!address.StartsWith('['))
        {
            return address.IndexOf(':');
        }

        var afterBracket = address.IndexOf(']') + 1;
        if (afterBracket == 0)
        {
            return InvalidBrackets;
        }

        if (afterBracket == address.Length)
        {
            return -1;
        }

        return address[afterBracket] == ':' ? afterBracket : InvalidBrackets;
    }

    private static bool TryParsePort(ReadOnlySpan<char> text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>
    /// Gets the host of a <see cref="NormalizeHostAddress">normalized host address</see>, without brackets and port.
    /// </summary>
    public static string GetHost(string normalizedHostAddress)
    {
        var bracketEnd = normalizedHostAddress.IndexOf(']');
        return normalizedHostAddress.StartsWith('[') && bracketEnd > 0
            ? normalizedHostAddress[1..bracketEnd]
            : normalizedHostAddress[..normalizedHostAddress.LastIndexOf(':')];
    }

    public Task<NukiInfoResponse> GetInfoAsync(CancellationToken cancellationToken) =>
        GetAsync("info", null, ReadTimeout, NukiJsonContext.Default.NukiInfoResponse, RequestKind.Read, cancellationToken);

    public Task<NukiDeviceResponse[]> GetListAsync(CancellationToken cancellationToken) =>
        GetAsync("list", null, ReadTimeout, NukiJsonContext.Default.NukiDeviceResponseArray, RequestKind.Read, cancellationToken);

    /// <summary>
    /// Runs a lock action and waits until the device has finished it. Not sent again when the bridge answers 503, as
    /// the device may run the action late.
    /// </summary>
    public Task<NukiActionResponse> LockActionAsync(long nukiId, int deviceType, int action, CancellationToken cancellationToken) =>
        GetAsync(
            "lockAction",
            string.Create(CultureInfo.InvariantCulture, $"nukiId={nukiId}&deviceType={deviceType}&action={action}&nowait=0"),
            ActionTimeout,
            NukiJsonContext.Default.NukiActionResponse,
            RequestKind.Action,
            cancellationToken);

    /// <summary>
    /// Asks the bridge for its token. The bridge answers once its button is pressed, within 30 seconds.
    /// </summary>
    public Task<NukiAuthResponse> AuthAsync(CancellationToken cancellationToken) =>
        GetAsync("auth", null, AuthTimeout, NukiJsonContext.Default.NukiAuthResponse, RequestKind.Auth, cancellationToken);

    private async Task<T> GetAsync<T>(
        string path, string? query, TimeSpan timeout, JsonTypeInfo<T> typeInfo, RequestKind kind, CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                // Built per attempt: an encrypted token is valid for one request only.
                var address = BuildAddress(path, query, authenticate: kind != RequestKind.Auth);
                using var response = await _httpClient.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && kind != RequestKind.Action && attempt == 1)
                {
                    attempt++;
                    await Task.Delay(_busyRetryDelay, cancellationToken);
                    continue;
                }

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
    }

    private string BuildAddress(string path, string? query, bool authenticate)
    {
        var builder = new StringBuilder(_baseAddress, 256).Append(path);
        var separator = '?';
        if (query is not null)
        {
            builder.Append(separator).Append(query);
            separator = '&';
        }

        if (authenticate)
        {
            builder.Append(separator);
            AppendAuthentication(builder);
        }

        return builder.ToString();
    }

    private void AppendAuthentication(StringBuilder builder)
    {
        if (_tokenMode != NukiTokenMode.Encrypted || _token is null)
        {
            builder.Append("token=").Append(Uri.EscapeDataString(_token ?? string.Empty));
            return;
        }

        AppendEncryptedToken(builder, _token);
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

    private enum RequestKind
    {
        /// <summary>Reads state with the token; sent once more when the bridge is busy.</summary>
        Read,

        /// <summary>Runs a lock action with the token; never sent twice.</summary>
        Action,

        /// <summary>Asks for the token, so carries none; sent once more when the bridge is busy.</summary>
        Auth
    }
}
