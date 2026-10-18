using System.Net.Http.Json;
using System.Text.Json;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverClient
{
    private const string BaseAddress = "https://api.pushover.net/1/";

    private readonly IHttpClientFactory _httpClientFactory;

    // Pushover accepts at most two concurrent connections per application, and each service has its own client.
    private readonly SemaphoreSlim _connections = new(2, 2);

    public PushoverClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<PushoverResponse> SendMessageAsync(IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken) =>
        SendAsync("messages.json", new FormUrlEncodedContent(fields), cancellationToken);

    public Task<PushoverResponse> ValidateUserAsync(string token, string userKey, CancellationToken cancellationToken) =>
        SendAsync("users/validate.json",
            new FormUrlEncodedContent([new("token", token), new("user", userKey)]),
            cancellationToken);

    private async Task<PushoverResponse> SendAsync(string path, HttpContent content, CancellationToken cancellationToken)
    {
        await _connections.WaitAsync(cancellationToken);
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseAddress + path) { Content = content };
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
