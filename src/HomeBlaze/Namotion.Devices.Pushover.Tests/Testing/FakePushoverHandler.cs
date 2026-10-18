using System.Net;
using System.Text;

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
    /// The content type of every response; override to simulate a non-JSON error page.
    /// </summary>
    public string ResponseContentType { get; set; } = "application/json";

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
        var contentType = request.Content?.Headers.ContentType?.MediaType;
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, contentType);
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
                Content = new StringContent(responseBody, Encoding.UTF8, ResponseContentType)
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

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? ContentType)
{
    public string Path => Uri.AbsolutePath;

    /// <summary>
    /// The request body parsed as <c>application/x-www-form-urlencoded</c> fields.
    /// </summary>
    public IReadOnlyDictionary<string, string> Form => ParseForm(Body ?? string.Empty);

    private static IReadOnlyDictionary<string, string> ParseForm(string body)
    {
        var fields = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            fields[key] = value;
        }

        return fields;
    }
}
