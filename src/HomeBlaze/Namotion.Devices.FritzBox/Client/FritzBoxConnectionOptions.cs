namespace Namotion.Devices.FritzBox.Client;

/// <param name="HostAddress">The host name or IP address of the router.</param>
/// <param name="Username">The user to log in as, or null for none.</param>
/// <param name="Password">The password of the user, or null for none.</param>
/// <param name="CertificatePin">The pinned key, or null to accept the key the router presents.</param>
internal sealed record FritzBoxConnectionOptions(string HostAddress, string? Username, string? Password, string? CertificatePin)
{
    internal int DescriptionPort { get; init; } = Tr64Description.DescriptionPort;

    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
