// Derived from hyundai_kia_connect_api (MIT License, Copyright (c) 2021, Fuat Akgun); see THIRD-PARTY-NOTICES.md.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// Signs the CCI auth requests like the OneApp: HMAC-SHA256 keyed with the client identity and the app key pieces, over
/// the identity, the OS version, <c>X-Timestamp</c> and the body without ASCII whitespace, as lowercase hex.
/// </summary>
internal static class BluelinkFingerprint
{
    private const string Identity =
        BluelinkAppConstants.CciPackageId + BluelinkAppConstants.CciClientName +
        BluelinkAppConstants.CciClientVersion + BluelinkAppConstants.CciClientOsCode;

    private static readonly byte[] Key = Encoding.UTF8.GetBytes(Identity + BluelinkAppConstants.FingerprintKeyPieces);

    public static string Compute(string timestamp, string body)
    {
        var message = new StringBuilder(Identity.Length + BluelinkAppConstants.CciClientOsVersion.Length + timestamp.Length + body.Length)
            .Append(Identity)
            .Append(BluelinkAppConstants.CciClientOsVersion)
            .Append(timestamp);

        foreach (var character in body)
        {
            // Python's \s with re.ASCII: space, plus tab, newline, vertical tab, form feed and carriage return (U+0009 to U+000D).
            if (character is not (' ' or (>= '\t' and <= '\r')))
            {
                message.Append(character);
            }
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(message.ToString())));
    }

    /// <summary>Adds the OneApp identity headers, <c>X-Timestamp</c> and <c>X-Fingerprint</c> for the exact body that is sent.</summary>
    public static void AddHeaders(HttpRequestMessage request, string deviceId, string body, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var timestamp = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var offset = timeProvider.LocalTimeZone.GetUtcOffset(now);
        var headers = request.Headers;

        headers.TryAddWithoutValidation("client-id", BluelinkAppConstants.CciPackageId);
        headers.TryAddWithoutValidation("client-name", BluelinkAppConstants.CciClientName);
        headers.TryAddWithoutValidation("client-version", BluelinkAppConstants.CciClientVersion);
        headers.TryAddWithoutValidation("client-os-code", BluelinkAppConstants.CciClientOsCode);
        headers.TryAddWithoutValidation("client-os-version", BluelinkAppConstants.CciClientOsVersion);
        headers.TryAddWithoutValidation("client-device-id", deviceId);
        headers.TryAddWithoutValidation("client-device-model", BluelinkAppConstants.CciDeviceModel);
        headers.TryAddWithoutValidation("client-notification-provider-type", BluelinkAppConstants.CciNotificationProvider);
        headers.TryAddWithoutValidation("locale", BluelinkAppConstants.Language.ToUpperInvariant());
        headers.TryAddWithoutValidation("timezone", (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        headers.TryAddWithoutValidation("App-Request-Id", Guid.NewGuid().ToString("N")[..24].ToUpperInvariant() + "_" + deviceId.Replace("-", "", StringComparison.Ordinal) + "_AOS");
        headers.TryAddWithoutValidation("X-Brand", BluelinkAppConstants.Brand);
        headers.TryAddWithoutValidation("X-Region", BluelinkAppConstants.Region);
        headers.TryAddWithoutValidation("User-Agent", BluelinkAppConstants.CciUserAgent);
        headers.TryAddWithoutValidation("Accept", "*/*");
        headers.TryAddWithoutValidation("Accept-Language", BluelinkAppConstants.Language);
        headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        headers.TryAddWithoutValidation("X-Fingerprint", Compute(timestamp, body));
    }
}
