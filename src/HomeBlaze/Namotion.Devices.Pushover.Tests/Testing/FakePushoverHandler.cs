using System.Net;
using System.Text;
using System.Web;

namespace Namotion.Devices.Pushover.Tests.Testing;

/// <summary>
/// Records the requests to the Pushover API and answers them with <see cref="Respond"/>.
/// </summary>
internal sealed class FakePushoverHandler : HttpMessageHandler
{
    public const string Success = """{"status":1,"request":"test"}""";

    /// <summary>
    /// A responder for the common emergency flow: sending a message always returns receipt "receipt1", polling its
    /// receipt returns <paramref name="receiptBody"/>, and every other request succeeds.
    /// </summary>
    public static Func<RecordedRequest, (HttpStatusCode StatusCode, string Body)> RespondWithReceipt(string receiptBody) =>
        request => request.Path switch
        {
            "/1/messages.json" => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}"""),
            "/1/receipts/receipt1.json" => (HttpStatusCode.OK, receiptBody),
            _ => (HttpStatusCode.OK, Success)
        };

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
        var query = HttpUtility.ParseQueryString(body);
        return query.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => query[key] ?? string.Empty);
    }
}
