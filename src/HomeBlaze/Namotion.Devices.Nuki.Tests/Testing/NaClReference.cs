using Namotion.Devices.Nuki.Client;

namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// The secretbox construction built from the primitives of NaCl.Core, as a reference for <see cref="SecretBox"/>:
/// XSalsa20 over 32 zero bytes and the message gives the Poly1305 key and the ciphertext.
/// </summary>
internal static class NaClReference
{
    private const int PolyKeySize = 32;

    /// <summary>Encrypts a message to the tag followed by the ciphertext.</summary>
    public static byte[] Seal(byte[] message, byte[] nonce, byte[] key)
    {
        var stream = new byte[PolyKeySize + message.Length];
        message.CopyTo(stream.AsSpan(PolyKeySize));
        using var cipher = new NaCl.Core.XSalsa20(key, 0);
        cipher.Encrypt(stream, nonce, stream);

        var result = new byte[SecretBox.TagSize + message.Length];
        stream.AsSpan(PolyKeySize).CopyTo(result.AsSpan(SecretBox.TagSize));
        NaCl.Core.Poly1305.ComputeMac(stream.AsSpan(0, PolyKeySize), stream.AsSpan(PolyKeySize), result.AsSpan(0, SecretBox.TagSize));
        return result;
    }

    /// <summary>Verifies the tag of a sealed message and decrypts it.</summary>
    public static byte[] Open(byte[] sealedMessage, byte[] nonce, byte[] key)
    {
        var stream = new byte[PolyKeySize + sealedMessage.Length - SecretBox.TagSize];
        sealedMessage.AsSpan(SecretBox.TagSize).CopyTo(stream.AsSpan(PolyKeySize));
        using var cipher = new NaCl.Core.XSalsa20(key, 0);
        cipher.Encrypt(stream, nonce, stream);
        NaCl.Core.Poly1305.VerifyMac(
            stream.AsSpan(0, PolyKeySize), sealedMessage.AsSpan(SecretBox.TagSize), sealedMessage.AsSpan(0, SecretBox.TagSize));
        return stream[PolyKeySize..];
    }
}
