using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkPasswordEncryptorTests
{
    [Fact]
    public void WhenPasswordIsEncrypted_ThenThePrivateKeyDecryptsTheLowercaseHex()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var modulus = Base64Url.EncodeToString(parameters.Modulus);
        var exponent = Base64Url.EncodeToString(parameters.Exponent);

        // Act
        var encrypted = BluelinkPasswordEncryptor.Encrypt("secret-pässword", modulus, exponent);

        // Assert
        Assert.Equal(encrypted.ToLowerInvariant(), encrypted);
        var decrypted = rsa.Decrypt(Convert.FromHexString(encrypted), RSAEncryptionPadding.Pkcs1);
        Assert.Equal("secret-pässword", Encoding.UTF8.GetString(decrypted));
    }
}
