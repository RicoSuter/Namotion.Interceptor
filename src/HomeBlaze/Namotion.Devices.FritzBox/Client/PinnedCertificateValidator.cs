using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// Accepts the router's certificate by its public key pin instead of its chain, and records each certificate the
/// router presents. Thread-safe.
/// </summary>
/// <param name="pin">The pinned key, or null to accept any key.</param>
internal sealed class PinnedCertificateValidator(string? pin)
{
    private CertificateInfo? _presented;
    private volatile bool _isRejected;

    /// <summary>
    /// The certificate presented at the last handshake, null before the first.
    /// </summary>
    internal CertificateInfo? Presented => Volatile.Read(ref _presented);

    /// <summary>
    /// Whether a handshake presented a key other than the pinned one.
    /// </summary>
    internal bool IsRejected => _isRejected;

    /// <summary>
    /// The <see cref="RemoteCertificateValidationCallback"/>: the router's certificate is self-signed and not issued
    /// for its address, so the policy errors are ignored and the pinned key alone decides.
    /// </summary>
    internal bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        var presented = CertificateInfo.Create(certificate);
        Volatile.Write(ref _presented, presented);
        var isAccepted = pin is null || string.Equals(presented.Pin, pin, StringComparison.Ordinal);
        if (!isAccepted)
        {
            _isRejected = true;
        }

        return isAccepted;
    }
}
