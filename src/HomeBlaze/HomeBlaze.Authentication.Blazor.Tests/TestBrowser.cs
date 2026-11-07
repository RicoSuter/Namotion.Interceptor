using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// A client of the test server that keeps cookies like a browser, follows no redirect and posts the forms of the account
/// dialogs as their script does, with the antiforgery token of the visitor.
/// </summary>
internal sealed partial class TestBrowser : IDisposable
{
    public const string SessionCookieName = "HomeBlaze.Session";

    private readonly CookieContainer _cookies = new();
    private readonly Uri _baseAddress;
    private readonly HttpClient _client;

    public TestBrowser(HttpMessageHandler serverHandler, Uri baseAddress)
    {
        _baseAddress = baseAddress;
        _client = new HttpClient(new CookieHandler(_cookies) { InnerHandler = serverHandler }) { BaseAddress = baseAddress };
    }

    /// <summary>Whether the session cookie is set.</summary>
    public bool HasSessionCookie => _cookies.GetCookies(_baseAddress)[SessionCookieName] is not null;

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path);

    public Task<HttpResponseMessage> PostAsync(string path, IReadOnlyDictionary<string, string> fields) =>
        _client.PostAsync(path, new FormUrlEncodedContent(fields));

    /// <summary>
    /// Posts <paramref name="fields"/> to the account endpoint at <paramref name="path"/> as the script of a dialog does:
    /// accepting JSON, as multipart form data with the antiforgery token of the visitor now, unless
    /// <paramref name="withToken"/> is false.
    /// </summary>
    public async Task<HttpResponseMessage> PostAccountFormAsync(string path, IReadOnlyDictionary<string, string> fields, bool withToken = true)
    {
        var form = withToken ? await GetHiddenFieldsAsync(AntiforgeryTestPage.Path) : [];
        foreach (var (name, value) in fields)
        {
            form[name] = value;
        }

        using var content = new MultipartFormDataContent();
        foreach (var (name, value) in form)
        {
            content.Add(new StringContent(value), name);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _client.SendAsync(request);
    }

    /// <summary>Gets the page to load of the answer of an account endpoint, which must succeed.</summary>
    public static async Task<string> GetAccountRedirectAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return answer.RootElement.GetProperty("redirect").GetString()!;
    }

    /// <summary>Gets the messages of the answer of an account endpoint, which must be refused with 400.</summary>
    public static async Task<IReadOnlyList<string>> GetAccountErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return answer.RootElement.TryGetProperty("error", out var error)
            ? [error.GetString()!]
            : [.. answer.RootElement.GetProperty("errors").EnumerateArray().Select(message => message.GetString()!)];
    }

    /// <summary>Gets the page at <paramref name="path"/> and returns the hidden fields of its forms.</summary>
    public async Task<Dictionary<string, string>> GetHiddenFieldsAsync(string path)
    {
        using var page = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var fields = new Dictionary<string, string>();
        foreach (Match input in HiddenInput().Matches(html))
        {
            var name = NameAttribute().Match(input.Value);
            var value = ValueAttribute().Match(input.Value);
            if (name.Success)
            {
                fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : string.Empty;
            }
        }

        return fields;
    }

    /// <summary>Gets the path and query of the <c>Location</c> header of a redirect.</summary>
    public static string GetRedirectPath(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("name=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttribute();

    [GeneratedRegex("value=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttribute();

    private sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var header = cookies.GetCookieHeader(uri);
            if (header.Length > 0)
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    cookies.SetCookies(uri, setCookie);
                }
            }

            return response;
        }
    }
}
