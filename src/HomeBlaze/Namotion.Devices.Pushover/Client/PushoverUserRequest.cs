namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverUserRequest
{
    public required string Token { get; init; }

    public required string User { get; init; }

    /// <summary>
    /// The Pushover user validation endpoint documents form fields, not a JSON body.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> ToFormFields()
    {
        yield return new KeyValuePair<string, string>("token", Token);
        yield return new KeyValuePair<string, string>("user", User);
    }
}
