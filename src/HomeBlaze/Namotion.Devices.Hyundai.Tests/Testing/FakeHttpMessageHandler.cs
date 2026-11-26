using System.Net;
using System.Text;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>A request received by <see cref="FakeHttpMessageHandler"/>.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string PathAndQuery => Uri.PathAndQuery;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Answers requests from a queue of responses per path and records every request. The last queued response of a path
/// repeats; an unknown path answers 404.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<Func<CancellationToken, Task<HttpResponseMessage>>>> _responses = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public FakeHttpMessageHandler Respond(string path, HttpStatusCode statusCode, string body, params (string Name, string Value)[] headers) =>
        Respond(path, () =>
        {
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        });

    public FakeHttpMessageHandler Respond(string path, Func<HttpResponseMessage> factory) =>
        Enqueue(path, _ => Task.FromResult(factory()));

    /// <summary>
    /// Answers the path with the response once the task completes, for example to hold a request while a test acts. A
    /// canceled request stops waiting, as a real one does.
    /// </summary>
    public FakeHttpMessageHandler RespondAfter(string path, Task<HttpResponseMessage> response) =>
        Enqueue(path, cancellationToken => response.WaitAsync(cancellationToken));

    /// <summary>Holds the request until it is canceled, as a car that never answers.</summary>
    public FakeHttpMessageHandler RespondNever(string path) =>
        Enqueue(path, async cancellationToken =>
        {
            var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
            return await never.Task;
        });

    private FakeHttpMessageHandler Enqueue(string path, Func<CancellationToken, Task<HttpResponseMessage>> factory)
    {
        lock (_lock)
        {
            if (!_responses.TryGetValue(path, out var queue))
            {
                _responses[path] = queue = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>();
            }

            queue.Enqueue(factory);
        }

        return this;
    }

    /// <summary>Removes the queued responses of a path, so that the next <see cref="Respond(string, Func{HttpResponseMessage})"/> answers first.</summary>
    public FakeHttpMessageHandler Reset(string path)
    {
        lock (_lock)
        {
            _responses.Remove(path);
        }

        return this;
    }

    public int Count(string path) => Requests.Count(request => request.Uri.AbsolutePath == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);

        Func<CancellationToken, Task<HttpResponseMessage>>? factory = null;
        lock (_lock)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, headers));
            if (_responses.TryGetValue(request.RequestUri!.AbsolutePath, out var queue) && queue.Count > 0)
            {
                factory = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
            }
        }

        var response = factory is null ? null : await factory(cancellationToken);
        response ??= new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        response.RequestMessage = request;
        return response;
    }
}
