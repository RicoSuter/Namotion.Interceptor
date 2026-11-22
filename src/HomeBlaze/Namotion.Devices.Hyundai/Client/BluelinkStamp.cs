// Derived from hyundai_kia_connect_api (MIT License, Copyright (c) 2021, Fuat Akgun); see THIRD-PARTY-NOTICES.md.
using System.Globalization;
using System.Text;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>Creates the <c>Stamp</c> header of the vehicle API.</summary>
internal static class BluelinkStamp
{
    private static readonly byte[] Cipher = Convert.FromBase64String(BluelinkAppConstants.StampCipher);

    /// <summary>Returns base64 of the cipher XOR <c>"{application ID}:{Unix seconds}"</c>, as long as the shorter of both.</summary>
    public static string Create(DateTimeOffset now)
    {
        var plain = Encoding.ASCII.GetBytes(
            BluelinkAppConstants.ApplicationId + ":" + now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var length = Math.Min(plain.Length, Cipher.Length);
        var stamp = new byte[length];
        for (var index = 0; index < length; index++)
        {
            stamp[index] = (byte)(plain[index] ^ Cipher[index]);
        }

        return Convert.ToBase64String(stamp);
    }
}
