namespace Namotion.Matter;

/// <summary>A decoded QR code payload or manual pairing code.</summary>
/// <param name="SetupPinCode">The setup PIN code.</param>
/// <param name="Discriminator">The 12-bit discriminator, from a QR code; null for a manual code.</param>
/// <param name="ShortDiscriminator">The 4-bit discriminator, from a manual code; null for a QR code.</param>
/// <param name="VendorId">The vendor ID, or null when the code does not state it.</param>
/// <param name="ProductId">The product ID, or null when the code does not state it.</param>
/// <param name="DiscoveryCapabilities">How the device can be found, from a QR code; null for a manual code.</param>
public sealed record MatterOnboardingPayload(
    uint SetupPinCode,
    ushort? Discriminator,
    byte? ShortDiscriminator,
    ushort? VendorId,
    ushort? ProductId,
    MatterDiscoveryCapabilities? DiscoveryCapabilities);

/// <summary>How a device can be found for commissioning.</summary>
[Flags]
public enum MatterDiscoveryCapabilities
{
    /// <summary>No capability stated.</summary>
    None = 0,

    /// <summary>Over a Wi-Fi soft access point.</summary>
    SoftAccessPoint = 1,

    /// <summary>Over Bluetooth Low Energy.</summary>
    Bluetooth = 2,

    /// <summary>On the IP network the controller is on.</summary>
    OnNetwork = 4
}
