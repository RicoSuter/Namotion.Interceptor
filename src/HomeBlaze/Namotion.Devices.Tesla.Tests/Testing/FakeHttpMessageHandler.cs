using System.Net;
using System.Text;

namespace Namotion.Devices.Tesla.Tests.Testing;

/// <summary>
/// Answers requests from a queue of responses per path and records every request with its body.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<Func<Task<HttpResponseMessage>>>> _responses = new();
    private readonly List<(HttpMethod Method, string PathAndQuery, string? Body, string? Authorization)> _requests = [];

    /// <summary>A snapshot of the requests received so far.</summary>
    public IReadOnlyList<(HttpMethod Method, string PathAndQuery, string? Body, string? Authorization)> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public FakeHttpMessageHandler Respond(string path, HttpStatusCode statusCode, string body, params (string Name, string Value)[] headers)
    {
        var factory = () =>
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
        };

        return Respond(path, () => Task.FromResult(factory()));
    }

    /// <summary>Queues a response that the test completes later, for example to hold a request back.</summary>
    public FakeHttpMessageHandler Respond(string path, Func<Task<HttpResponseMessage>> factory)
    {
        lock (_lock)
        {
            if (!_responses.TryGetValue(path, out var queue))
            {
                _responses[path] = queue = new Queue<Func<Task<HttpResponseMessage>>>();
            }

            queue.Enqueue(factory);
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;

        Func<Task<HttpResponseMessage>>? factory = null;
        lock (_lock)
        {
            _requests.Add((request.Method, request.RequestUri.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            if (_responses.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                factory = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
            }
        }

        if (factory is not null)
        {
            // Like a real handler, a held-back request ends when the client cancels it or is disposed.
            return await factory().WaitAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
    }
}
