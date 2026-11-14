using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Namotion.Devices.Nuki.Client;

/// <summary>
/// Encrypts with the NaCl secretbox (XSalsa20-Poly1305) and returns the tag followed by the ciphertext, the layout of
/// <c>crypto_secretbox_easy</c>.
/// </summary>
internal static class SecretBox
{
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    private const int MacKeySize = 32;

    /// <summary>
    /// Encrypts and authenticates the message.
    /// </summary>
    /// <param name="message">The message to encrypt.</param>
    /// <param name="nonce">A nonce of <see cref="NonceSize"/> bytes that is never used twice with the same key.</param>
    /// <param name="key">The key of <see cref="KeySize"/> bytes.</param>
    /// <returns>The tag of <see cref="TagSize"/> bytes followed by the ciphertext.</returns>
    public static byte[] Seal(ReadOnlySpan<byte> message, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, KeySize);
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceSize);

        Span<uint> state = stackalloc uint[16];
        Span<uint> words = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        Span<byte> subKey = stackalloc byte[KeySize];
        Span<byte> macKey = stackalloc byte[MacKeySize];

        // XSalsa20: HSalsa20 derives a subkey from the first 16 nonce bytes, Salsa20 runs with it on the last 8.
        InitializeState(state, key, nonce[..16]);
        Core(state, words, addInput: false);
        ReadOnlySpan<int> subKeyWords = [0, 5, 10, 15, 6, 7, 8, 9];
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(subKey[(i * 4)..], words[subKeyWords[i]]);
        }

        Span<byte> nonceAndCounter = stackalloc byte[16];
        nonce[16..].CopyTo(nonceAndCounter);
        InitializeState(state, subKey, nonceAndCounter);

        // The first MacKeySize key stream bytes are the Poly1305 key, the message is encrypted with the stream after them.
        var result = new byte[TagSize + message.Length];
        var ciphertext = result.AsSpan(TagSize);
        var streamLength = MacKeySize + message.Length;
        for (var position = 0; position < streamLength; position += 64)
        {
            state[8] = (uint)(position / 64);
            Core(state, words, addInput: true);
            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], words[i]);
            }

            for (var i = 0; i < 64 && position + i < streamLength; i++)
            {
                var index = position + i;
                if (index < MacKeySize)
                {
                    macKey[index] = block[i];
                }
                else
                {
                    ciphertext[index - MacKeySize] = (byte)(message[index - MacKeySize] ^ block[i]);
                }
            }
        }

        ComputePoly1305(ciphertext, macKey, result.AsSpan(0, TagSize));

        // Clear everything that held key material or a derived secret: the HSalsa20/Salsa20 state and its last
        // output words (which held the subkey and the final key stream block, i.e. the Poly1305 key).
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state));
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(words));
        CryptographicOperations.ZeroMemory(block);
        CryptographicOperations.ZeroMemory(subKey);
        CryptographicOperations.ZeroMemory(macKey);
        return result;
    }

    private static void InitializeState(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        state[0] = 0x61707865;
        state[5] = 0x3320646e;
        state[10] = 0x79622d32;
        state[15] = 0x6b206574;
        for (var i = 0; i < 4; i++)
        {
            state[1 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
            state[11 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(16 + i * 4)..]);
            state[6 + i] = BinaryPrimitives.ReadUInt32LittleEndian(input[(i * 4)..]);
        }
    }

    // The Salsa20 core with 20 rounds. HSalsa20 is the same core without adding the input at the end.
    private static void Core(ReadOnlySpan<uint> input, Span<uint> x, bool addInput)
    {
        input.CopyTo(x);
        for (var round = 0; round < 10; round++)
        {
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 5, 9, 13, 1);
            QuarterRound(x, 10, 14, 2, 6);
            QuarterRound(x, 15, 3, 7, 11);
            QuarterRound(x, 0, 1, 2, 3);
            QuarterRound(x, 5, 6, 7, 4);
            QuarterRound(x, 10, 11, 8, 9);
            QuarterRound(x, 15, 12, 13, 14);
        }

        if (addInput)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] += input[i];
            }
        }
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= BitOperations.RotateLeft(x[a] + x[d], 7);
        x[c] ^= BitOperations.RotateLeft(x[b] + x[a], 9);
        x[d] ^= BitOperations.RotateLeft(x[c] + x[b], 13);
        x[a] ^= BitOperations.RotateLeft(x[d] + x[c], 18);
    }

    // Poly1305 with 26-bit limbs (poly1305-donna-32). No branch depends on the key or the message content, only on
    // the public message length.
    private static void ComputePoly1305(ReadOnlySpan<byte> message, ReadOnlySpan<byte> key, Span<byte> tag)
    {
        const uint Mask = 0x3ffffff;
        uint r0 = Read(key, 0) & 0x3ffffff, r1 = (Read(key, 3) >> 2) & 0x3ffff03, r2 = (Read(key, 6) >> 4) & 0x3ffc0ff;
        uint r3 = (Read(key, 9) >> 6) & 0x3f03fff, r4 = (Read(key, 12) >> 8) & 0x00fffff;
        uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
        uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

        Span<byte> buffer = stackalloc byte[16];
        for (var offset = 0; offset < message.Length; offset += 16)
        {
            var length = Math.Min(16, message.Length - offset);
            buffer.Clear();
            message.Slice(offset, length).CopyTo(buffer);
            var highBit = 1u << 24;
            if (length < 16)
            {
                buffer[length] = 1;
                highBit = 0;
            }

            h0 += Read(buffer, 0) & Mask;
            h1 += (Read(buffer, 3) >> 2) & Mask;
            h2 += (Read(buffer, 6) >> 4) & Mask;
            h3 += (Read(buffer, 9) >> 6) & Mask;
            h4 += (Read(buffer, 12) >> 8) | highBit;

            var d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
            var d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
            var d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
            var d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
            var d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

            d1 += d0 >> 26; h0 = (uint)d0 & Mask;
            d2 += d1 >> 26; h1 = (uint)d1 & Mask;
            d3 += d2 >> 26; h2 = (uint)d2 & Mask;
            d4 += d3 >> 26; h3 = (uint)d3 & Mask;
            h0 += (uint)(d4 >> 26) * 5; h4 = (uint)d4 & Mask;
            h1 += h0 >> 26; h0 &= Mask;
        }

        // Carry fully, then subtract p = 2^130 - 5 when h >= p.
        var carry = h1 >> 26; h1 &= Mask; h2 += carry;
        carry = h2 >> 26; h2 &= Mask; h3 += carry;
        carry = h3 >> 26; h3 &= Mask; h4 += carry;
        carry = h4 >> 26; h4 &= Mask; h0 += carry * 5;
        carry = h0 >> 26; h0 &= Mask; h1 += carry;

        var g0 = h0 + 5; carry = g0 >> 26; g0 &= Mask;
        var g1 = h1 + carry; carry = g1 >> 26; g1 &= Mask;
        var g2 = h2 + carry; carry = g2 >> 26; g2 &= Mask;
        var g3 = h3 + carry; carry = g3 >> 26; g3 &= Mask;
        var g4 = h4 + carry - (1u << 26);

        var select = (g4 >> 31) - 1;
        h0 = (h0 & ~select) | (g0 & select);
        h1 = (h1 & ~select) | (g1 & select);
        h2 = (h2 & ~select) | (g2 & select);
        h3 = (h3 & ~select) | (g3 & select);
        h4 = (h4 & ~select) | (g4 & select);

        // tag = (h + s) mod 2^128, where s is the second half of the key.
        var f = (ulong)(h0 | (h1 << 26)) + Read(key, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)f);
        f = (ulong)((h1 >> 6) | (h2 << 20)) + Read(key, 20) + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[4..], (uint)f);
        f = (ulong)((h2 >> 12) | (h3 << 14)) + Read(key, 24) + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[8..], (uint)f);
        f = (ulong)((h3 >> 18) | (h4 << 8)) + Read(key, 28) + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[12..], (uint)f);
    }

    private static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
