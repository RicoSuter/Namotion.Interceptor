using System.Text;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// The options of a connection. <see cref="ToString"/> never includes the password.
/// </summary>
/// <param name="HostAddress">The host name or IP address of the router.</param>
/// <param name="Username">The user to log in as, or null for none.</param>
/// <param name="Password">The password of the user, or null for none.</param>
/// <param name="CertificatePin">The pinned key, or null to trust the key the router presents first.</param>
internal sealed record FritzBoxConnectionOptions(string HostAddress, string? Username, string? Password, string? CertificatePin)
{
    internal int DescriptionPort { get; init; } = Tr64Description.DescriptionPort;

    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    private bool PrintMembers(StringBuilder builder)
    {
        builder
            .Append("HostAddress = ").Append(HostAddress)
            .Append(", Username = ").Append(Username)
            .Append(", CertificatePin = ").Append(CertificatePin);
        return true;
    }
}
