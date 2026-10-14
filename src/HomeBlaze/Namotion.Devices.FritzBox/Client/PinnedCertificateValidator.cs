using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// Accepts the router's certificate by its public key pin instead of its chain, and records each certificate the
/// router presents. Thread-safe.
/// </summary>
/// <param name="pin">The pinned key, or null to trust the key of the first handshake and only that key after it.</param>
internal sealed class PinnedCertificateValidator(string? pin)
{
    private string? _pin = pin;
    private CertificateInfo? _presented;
    private CertificateInfo? _rejected;

    /// <summary>
    /// The certificate presented at the last handshake, null before the first.
    /// </summary>
    internal CertificateInfo? Presented => Volatile.Read(ref _presented);

    /// <summary>
    /// The key every handshake must present: the configured pin, or the key of the first handshake when none was
    /// configured; null before that handshake.
    /// </summary>
    internal string? TrustedPin => Volatile.Read(ref _pin);

    /// <summary>
    /// The certificate of the last handshake that presented a key other than the pinned one, null while none did.
    /// </summary>
    internal CertificateInfo? Rejected => Volatile.Read(ref _rejected);

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
        // Without a configured pin the first key wins, so a later handshake of the same connection cannot switch it.
        var expectedPin = Interlocked.CompareExchange(ref _pin, presented.Pin, null) ?? presented.Pin;
        var isAccepted = string.Equals(presented.Pin, expectedPin, StringComparison.Ordinal);
        if (!isAccepted)
        {
            Volatile.Write(ref _rejected, presented);
        }

        return isAccepted;
    }
}
