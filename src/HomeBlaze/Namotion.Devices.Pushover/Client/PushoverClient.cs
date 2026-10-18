using System.Net.Http.Json;
using System.Text.Json;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverClient
{
    internal const string BaseAddress = "https://api.pushover.net/1/";

    private readonly IHttpClientFactory _httpClientFactory;

    // Pushover accepts at most two concurrent connections per client.
    private readonly SemaphoreSlim _connections = new(2, 2);

    public PushoverClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<PushoverResponse> SendMessageAsync(PushoverMessageRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "messages.json",
            JsonContent.Create(request, PushoverJsonContext.Default.PushoverMessageRequest), cancellationToken);

    public Task<PushoverResponse> ValidateUserAsync(string token, string userKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "users/validate.json",
            JsonContent.Create(new PushoverUserRequest { Token = token, User = userKey }, PushoverJsonContext.Default.PushoverUserRequest),
            cancellationToken);

    // The receipts endpoint takes the token only as a query parameter.
    public Task<PushoverResponse> GetReceiptAsync(string token, string receipt, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"receipts/{Uri.EscapeDataString(receipt)}.json?token={Uri.EscapeDataString(token)}",
            content: null, cancellationToken);

    public Task<PushoverResponse> CancelReceiptAsync(string token, string receipt, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"receipts/{Uri.EscapeDataString(receipt)}/cancel.json",
            JsonContent.Create(new PushoverTokenRequest { Token = token }, PushoverJsonContext.Default.PushoverTokenRequest),
            cancellationToken);

    private async Task<PushoverResponse> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        await _connections.WaitAsync(cancellationToken);
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(method, BaseAddress + path) { Content = content };
            using var response = await client.SendAsync(request, cancellationToken);

            var body = await ReadResponseAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode || body is not { Status: 1 })
            {
                throw new PushoverException(response.StatusCode, body);
            }

            return body;
        }
        finally
        {
            _connections.Release();
        }
    }

    private static async Task<PushoverResponse?> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(PushoverJsonContext.Default.PushoverResponse, cancellationToken);
        }
        catch (JsonException)
        {
            // Rate limits and outages can answer with text; the status code then describes the failure.
            return null;
        }
    }
}
