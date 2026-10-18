namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverTokenRequest
{
    public required string Token { get; init; }

    /// <summary>
    /// The Pushover receipt cancellation endpoint documents form fields, not a JSON body.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> ToFormFields()
    {
        yield return new KeyValuePair<string, string>("token", Token);
    }
}
