using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Namotion.Devices.FritzBox.Client;

/// <param name="Pin">Base64 SHA-256 of the SubjectPublicKeyInfo; a certificate renewed with the same key keeps it.</param>
/// <param name="Fingerprint">SHA-256 of the whole certificate as colon-separated hex, the form browsers show.</param>
internal sealed record CertificateInfo(string Pin, string Fingerprint)
{
    internal static CertificateInfo Create(X509Certificate certificate)
    {
        if (certificate is X509Certificate2 certificate2)
        {
            return Create(certificate2);
        }

        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Create(loaded);
    }

    private static CertificateInfo Create(X509Certificate2 certificate)
    {
        var pin = Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        var hex = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var fingerprint = new StringBuilder(hex.Length + hex.Length / 2);
        for (var index = 0; index < hex.Length; index += 2)
        {
            if (index > 0)
            {
                fingerprint.Append(':');
            }

            fingerprint.Append(hex, index, 2);
        }

        return new CertificateInfo(pin, fingerprint.ToString());
    }
}
