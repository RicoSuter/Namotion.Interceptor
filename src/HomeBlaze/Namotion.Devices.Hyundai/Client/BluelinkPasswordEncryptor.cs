using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>Encrypts the password for the sign in with the RSA key of <c>/auth/api/v1/accounts/certs</c>.</summary>
internal static class BluelinkPasswordEncryptor
{
    /// <param name="password">The password, encoded as UTF-8.</param>
    /// <param name="modulus">The <c>n</c> of the JWK, base64url.</param>
    /// <param name="exponent">The <c>e</c> of the JWK, base64url.</param>
    /// <returns>The RSA PKCS#1 v1.5 cipher text as lowercase hex.</returns>
    /// <exception cref="BluelinkApiException">The key is malformed or cannot encrypt the password.</exception>
    public static string Encrypt(string password, string modulus, string exponent)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64Url.DecodeFromChars(modulus),
                Exponent = Base64Url.DecodeFromChars(exponent)
            });

            // Hyundai's sign in accepts the password only with RSA PKCS#1 v1.5.
#pragma warning disable S5542
            return Convert.ToHexStringLower(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1));
#pragma warning restore S5542
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new BluelinkApiException(HttpStatusCode.OK, null, "The login returned no usable RSA key.");
        }
    }
}
