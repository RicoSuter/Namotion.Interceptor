using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class SecretBoxTests
{
    // The secretbox vector of libsodium (test/default/secretbox.c and secretbox.exp).
    private static readonly byte[] VectorKey = Convert.FromHexString("1b27556473e985d462cd51197a9a46c76009549eac6474f206c4ee0844f68389");
    private static readonly byte[] VectorNonce = Convert.FromHexString("69696ee955b62b73cd62bda875fc73d68219e0036b7a0b37");
    private static readonly byte[] VectorMessage = Convert.FromHexString(
        "be075fc53c81f2d5cf141316ebeb0c7b5228c52a4c62cbd44b66849b64244ffce5ecbaaf33bd751a1ac728d45e6c61296cdc3c01233561f41db66cce314adb310e3be8250c46f06dceea3a7fa1348057e2f6556ad6b1318a024a838f21af1fde048977eb48f59ffd4924ca1c60902e52f0a089bc76897040e082f937763848645e0705");
    private static readonly byte[] VectorSealed = Convert.FromHexString(
        "f3ffc7703f9400e52a7dfb4b3d3305d98e993b9f48681273c29650ba32fc76ce48332ea7164d96a4476fb8c531a1186ac0dfc17c98dce87b4da7f011ec48c97271d2c20f9b928fe2270d6fb863d51738b48eeee314a7cc8ab932164548e526ae90224368517acfeabd6bb3732bc0e9da99832b61ca01b6de56244a9e88d5f9b37973f622a43d14a6599b1f654cb45a74e355a5");

    [Fact]
    public void WhenTheLibsodiumVectorIsSealed_ThenTheOutputIsTheTagFollowedByTheCiphertext()
    {
        // Act
        var result = SecretBox.Seal(VectorMessage, VectorNonce, VectorKey);

        // Assert
        Assert.Equal(VectorSealed, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(299)]
    public void WhenRandomMessagesAreSealed_ThenTheResultMatchesNaClCore(int length)
    {
        for (var run = 0; run < 50; run++)
        {
            // Arrange
            var random = new Random(length * 1000 + run);
            var message = new byte[length];
            var nonce = new byte[SecretBox.NonceSize];
            var key = new byte[SecretBox.KeySize];
            random.NextBytes(message);
            random.NextBytes(nonce);
            random.NextBytes(key);

            // Act
            var result = SecretBox.Seal(message, nonce, key);

            // Assert
            Assert.Equal(NaClReference.Seal(message, nonce, key), result);
        }
    }

    [Theory]
    [InlineData(16, SecretBox.NonceSize)]
    [InlineData(SecretBox.KeySize, 8)]
    public void WhenTheKeyOrNonceHasTheWrongLength_ThenSealThrows(int keyLength, int nonceLength)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => SecretBox.Seal([1, 2, 3], new byte[nonceLength], new byte[keyLength]));
    }
}
