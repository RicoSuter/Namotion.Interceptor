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
    private int _maxConcurrentRequests;
    private int _waitingListRequests;

    public volatile string InfoJson = NukiFixtures.Info;
    public volatile string ListJson = NukiFixtures.List;
    public volatile string ActionJson = NukiFixtures.ActionSucceeded;
    public volatile string AuthJson = """{"token": "abc123", "success": true}""";

    /// <summary>Gets the status to answer a path with instead of its content, or null to answer normally.</summary>
    public volatile Func<string, HttpStatusCode?>? StatusOverride;

    /// <summary>When set, a /lockAction request waits for it, like a bridge waits for the motor.</summary>
    /// <remarks>Create it with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, otherwise whatever
    /// completes the gate runs the waiting request's continuation inline.</remarks>
    public volatile TaskCompletionSource? ActionGate = null;

    /// <summary>When set, a /list request waits for it.</summary>
    /// <remarks>Create it with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, like <see cref="ActionGate"/>.</remarks>
    public volatile TaskCompletionSource? ListGate = null;

    public Uri[] Requests => _requests.ToArray();

    public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

    /// <summary>The /list requests that wait for <see cref="ListGate"/>.</summary>
    public int WaitingListRequests => Volatile.Read(ref _waitingListRequests);

    public int CountRequests(string path) => _requests.Count(uri => uri.AbsolutePath == "/" + path);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request.RequestUri!);
        var concurrent = Interlocked.Increment(ref _concurrentRequests);
        InterlockedMax(ref _maxConcurrentRequests, concurrent);
        try
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (StatusOverride?.Invoke(path) is { } status)
            {
                return new HttpResponseMessage(status);
            }

            if (path == "lockAction" && ActionGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            else if (path == "list" && ListGate is { } listGate)
            {
                Interlocked.Increment(ref _waitingListRequests);
                try
                {
                    await listGate.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _waitingListRequests);
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

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
