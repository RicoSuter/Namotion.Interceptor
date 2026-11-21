using System.Net;
using System.Text;

namespace Namotion.Devices.Tesla.Tests.Testing;

/// <summary>
/// Answers requests from a queue of responses per path and records every request with its body.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = new();

    public List<(HttpMethod Method, string PathAndQuery, string? Body, string? Authorization)> Requests { get; } = [];

    public FakeHttpMessageHandler Respond(string path, HttpStatusCode statusCode, string body, params (string Name, string Value)[] headers)
    {
        if (!_responses.TryGetValue(path, out var queue))
        {
            _responses[path] = queue = new Queue<Func<HttpResponseMessage>>();
        }

        queue.Enqueue(() =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));

        var path = request.RequestUri.AbsolutePath;
        if (_responses.TryGetValue(path, out var queue) && queue.Count > 0)
        {
            return queue.Count == 1 ? queue.Peek()() : queue.Dequeue()();
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
    }
}
