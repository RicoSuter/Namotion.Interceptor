using System.Net;
using System.Text;
using System.Text.Json;

namespace Namotion.Devices.Pushover.Tests.Testing;

/// <summary>
/// Records the requests to the Pushover API and answers them with <see cref="Respond"/>.
/// </summary>
internal sealed class FakePushoverHandler : HttpMessageHandler
{
    public const string Success = """{"status":1,"request":"test"}""";

    private readonly Lock _lock = new();
    private readonly List<RecordedRequest> _requests = [];
    private int _concurrentRequests;

    public Func<RecordedRequest, (HttpStatusCode StatusCode, string Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, Success);

    /// <summary>
    /// Awaited before each response, so a test can hold requests open.
    /// </summary>
    public Func<Task>? BeforeRespond { get; set; }

    public int MaximumConcurrentRequests { get; private set; }

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

    public RecordedRequest[] RequestsTo(string path) => Requests.Where(request => request.Path == path).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body);
        lock (_lock)
        {
            _requests.Add(recorded);
            _concurrentRequests++;
            MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, _concurrentRequests);
        }

        try
        {
            if (BeforeRespond is { } beforeRespond)
            {
                await beforeRespond();
            }

            var (statusCode, responseBody) = Respond(recorded);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
        finally
        {
            lock (_lock)
            {
                _concurrentRequests--;
            }
        }
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public string Path => Uri.AbsolutePath;

    public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
}
