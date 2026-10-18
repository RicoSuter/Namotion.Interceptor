using System.Buffers;
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

    /// <summary>The default delay before a request that the bridge answered with 503 is sent again.</summary>
    public static readonly TimeSpan DefaultBusyRetryDelay = TimeSpan.FromSeconds(1);

    private const int StackAllocTokenBytes = 256;

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
    /// Gets the host and port of a bridge, with the default port when none is given.
    /// </summary>
    public static string NormalizeHostAddress(string hostAddress)
    {
        var trimmed = hostAddress.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://".Length..];
        }

        trimmed = trimmed.TrimEnd('/');
        return trimmed.Contains(':') ? trimmed : trimmed + ":" + DefaultPort.ToString(CultureInfo.InvariantCulture);
    }

    public Task<NukiInfoResponse> GetInfoAsync(CancellationToken cancellationToken) =>
        GetAsync("info", null, ReadTimeout, NukiJsonContext.Default.NukiInfoResponse, authenticate: true, cancellationToken);

    public Task<NukiDeviceResponse[]> GetListAsync(CancellationToken cancellationToken) =>
        GetAsync("list", null, ReadTimeout, NukiJsonContext.Default.NukiDeviceResponseArray, authenticate: true, cancellationToken);

    /// <summary>
    /// Runs a lock action and waits until the device has finished it.
    /// </summary>
    public Task<NukiActionResponse> LockActionAsync(long nukiId, int deviceType, int action, CancellationToken cancellationToken) =>
        GetAsync(
            "lockAction",
            string.Create(CultureInfo.InvariantCulture, $"nukiId={nukiId}&deviceType={deviceType}&action={action}&nowait=0"),
            ActionTimeout,
            NukiJsonContext.Default.NukiActionResponse,
            authenticate: true,
            cancellationToken);

    /// <summary>
    /// Asks the bridge for its token. The bridge answers once its button is pressed, within 30 seconds.
    /// </summary>
    public Task<NukiAuthResponse> AuthAsync(CancellationToken cancellationToken) =>
        GetAsync("auth", null, AuthTimeout, NukiJsonContext.Default.NukiAuthResponse, authenticate: false, cancellationToken);

    private async Task<T> GetAsync<T>(
        string path, string? query, TimeSpan timeout, JsonTypeInfo<T> typeInfo, bool authenticate, CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                // Built per attempt: an encrypted token is valid for one request only.
                var address = BuildAddress(path, query, authenticate);
                using var response = await _httpClient.GetAsync(address, timeoutSource.Token);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 1)
                {
                    attempt++;
                    await Task.Delay(_busyRetryDelay, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateException(path, response.StatusCode);
                }

                return await response.Content.ReadFromJsonAsync(typeInfo, timeoutSource.Token)
                    ?? throw new InvalidDataException($"The bridge answered /{path} without content.");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
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
            $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'},{RandomNumberGenerator.GetInt32(ushort.MaxValue + 1)}"));
        Span<byte> nonce = stackalloc byte[SecretBox.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var maxTokenBytes = Encoding.UTF8.GetMaxByteCount(token.Length);
        var rented = maxTokenBytes > StackAllocTokenBytes ? ArrayPool<byte>.Shared.Rent(maxTokenBytes) : null;
        Span<byte> tokenBytes = rented ?? stackalloc byte[StackAllocTokenBytes];
        Span<byte> secretKey = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            var tokenByteCount = Encoding.UTF8.GetBytes(token, tokenBytes);
            SHA256.HashData(tokenBytes[..tokenByteCount], secretKey);
            var ctoken = SecretBox.Seal(plaintext, nonce, secretKey);

            builder.Append("ctoken=").Append(Convert.ToHexStringLower(ctoken))
                .Append("&nonce=").Append(Convert.ToHexStringLower(nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(secretKey);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
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
