using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// Answers the requests of a bridge client like a bridge does, and records them.
/// </summary>
internal sealed class FakeNukiBridge : HttpMessageHandler, IHttpClientFactory
{
    private readonly ConcurrentQueue<Uri> _requests = new();
    private int _concurrentRequests;
    private int _waitingRequests;
    private volatile bool _requestsOverlapped;

    public volatile string InfoJson = NukiFixtures.Info;
    public volatile string ListJson = NukiFixtures.List;
    public volatile string ActionJson = NukiFixtures.ActionSucceeded;
    public volatile string AuthJson = """{"token": "abc123", "success": true}""";

    /// <summary>Gets the status to answer a path with instead of its content, or null to answer normally.</summary>
    public volatile Func<string, HttpStatusCode?>? StatusOverride;

    /// <summary>
    /// Gets a task that a request to a path waits for before it is answered, like a bridge waits for the motor, or
    /// null to answer at once.
    /// </summary>
    /// <remarks>Complete a task of a <see cref="TaskCompletionSource"/> created with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, otherwise whatever completes it runs the
    /// waiting request's continuation inline.</remarks>
    public volatile Func<string, Task?>? Hold;

    public Uri[] Requests => _requests.ToArray();

    /// <summary>Whether two requests were ever answered at the same time.</summary>
    public bool RequestsOverlapped => _requestsOverlapped;

    /// <summary>The requests that wait for <see cref="Hold"/>.</summary>
    public int WaitingRequests => Volatile.Read(ref _waitingRequests);

    public int CountRequests(string path) => _requests.Count(uri => uri.AbsolutePath == "/" + path);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request.RequestUri!);
        if (Interlocked.Increment(ref _concurrentRequests) > 1)
        {
            _requestsOverlapped = true;
        }

        try
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (StatusOverride?.Invoke(path) is { } status)
            {
                return new HttpResponseMessage(status);
            }

            if (Hold?.Invoke(path) is { } hold)
            {
                Interlocked.Increment(ref _waitingRequests);
                try
                {
                    await hold.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _waitingRequests);
                }
            }

            var json = path switch
            {
                "info" => InfoJson,
                "list" => ListJson,
                "lockAction" => ActionJson,
                "auth" => AuthJson,
                _ => null
            };

            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentRequests);
        }
    }
}
