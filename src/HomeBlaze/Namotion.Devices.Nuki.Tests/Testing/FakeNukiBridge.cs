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

    public volatile string InfoJson = NukiFixtures.Info;
    public volatile string ListJson = NukiFixtures.List;
    public volatile string ActionJson = NukiFixtures.ActionSucceeded;
    public volatile string AuthJson = """{"token": "abc123", "success": true}""";

    /// <summary>Gets the status to answer a path with instead of its content, or null to answer normally.</summary>
    public volatile Func<string, HttpStatusCode?>? StatusOverride;

    /// <summary>Called when a /lockAction request arrives, before <see cref="ActionGate"/> is awaited.</summary>
    public volatile Action? OnAction = null;

    /// <summary>When set, a /lockAction request waits for it, like a bridge waits for the motor.</summary>
    /// <remarks>Create it with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, otherwise whatever
    /// completes the gate runs the waiting request's continuation inline.</remarks>
    public volatile TaskCompletionSource? ActionGate = null;

    public Uri[] Requests => _requests.ToArray();

    public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

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

            if (path == "lockAction")
            {
                OnAction?.Invoke();
                if (ActionGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
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
