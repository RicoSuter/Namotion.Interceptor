namespace Namotion.Devices.Nuki;

/// <summary>
/// How requests to the bridge carry the API token.
/// </summary>
public enum NukiTokenMode
{
    /// <summary>A token encrypted per request, valid once and for 60 seconds. Hardware bridges only.</summary>
    Encrypted,

    /// <summary>The token in plain text. Required by software bridges.</summary>
    Plain
}
