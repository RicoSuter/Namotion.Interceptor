using System.Globalization;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverMessageRequest
{
    public required string Token { get; init; }

    public required string User { get; init; }

    public required string Message { get; init; }

    public string? Title { get; init; }

    public int? Priority { get; init; }

    public string? Url { get; init; }

    public string? UrlTitle { get; init; }

    public int? Retry { get; init; }

    public int? Expire { get; init; }

    /// <summary>
    /// Comma-separated tags of an emergency notification, which cancel it together with others by tag.
    /// </summary>
    public string? Tags { get; init; }

    /// <summary>
    /// Returns the form fields of the messages endpoint, leaving out each member that is null.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> ToFormFields()
    {
        yield return new KeyValuePair<string, string>("token", Token);
        yield return new KeyValuePair<string, string>("user", User);
        yield return new KeyValuePair<string, string>("message", Message);

        if (Title is not null)
        {
            yield return new KeyValuePair<string, string>("title", Title);
        }

        if (Priority is { } priority)
        {
            yield return new KeyValuePair<string, string>("priority", priority.ToString(CultureInfo.InvariantCulture));
        }

        if (Url is not null)
        {
            yield return new KeyValuePair<string, string>("url", Url);
        }

        if (UrlTitle is not null)
        {
            yield return new KeyValuePair<string, string>("url_title", UrlTitle);
        }

        if (Retry is { } retry)
        {
            yield return new KeyValuePair<string, string>("retry", retry.ToString(CultureInfo.InvariantCulture));
        }

        if (Expire is { } expire)
        {
            yield return new KeyValuePair<string, string>("expire", expire.ToString(CultureInfo.InvariantCulture));
        }

        if (Tags is not null)
        {
            yield return new KeyValuePair<string, string>("tags", Tags);
        }
    }
}
