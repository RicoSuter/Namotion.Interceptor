using System.Net.Http.Json;
using System.Text.Json;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverClient
{
    internal const string BaseAddress = "https://api.pushover.net/1/";

    private readonly IHttpClientFactory _httpClientFactory;

    // Static, so the limit is shared per process: Pushover accepts at most two concurrent connections per
    // application, across every PushoverClient instance, not two per instance.
    private static readonly SemaphoreSlim Connections = new(2, 2);

    public PushoverClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<PushoverResponse> SendMessageAsync(PushoverMessageRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "messages.json", new FormUrlEncodedContent(request.ToFormFields()), cancellationToken);

    public Task<PushoverResponse> ValidateUserAsync(string token, string userKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "users/validate.json",
            new FormUrlEncodedContent([new("token", token), new("user", userKey)]),
            cancellationToken);

    // The receipts endpoint takes the token only as a query parameter.
    public Task<PushoverResponse> GetReceiptAsync(string token, string receipt, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"receipts/{Uri.EscapeDataString(receipt)}.json?token={Uri.EscapeDataString(token)}",
            content: null, cancellationToken);

    public Task<PushoverResponse> CancelReceiptAsync(string token, string receipt, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"receipts/{Uri.EscapeDataString(receipt)}/cancel.json",
            new FormUrlEncodedContent([new("token", token)]),
            cancellationToken);

    /// <summary>
    /// Stops the repeats of every emergency notification of the application that was sent with <paramref name="tag"/>.
    /// </summary>
    public Task<PushoverResponse> CancelByTagAsync(string token, string tag, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"receipts/cancel_by_tag/{Uri.EscapeDataString(tag)}.json",
            new FormUrlEncodedContent([new("token", token)]),
            cancellationToken);

    private async Task<PushoverResponse> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        await Connections.WaitAsync(cancellationToken);
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
            Connections.Release();
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
