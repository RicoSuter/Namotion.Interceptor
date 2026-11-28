namespace Namotion.Matter;

/// <summary>An open commissioning window through which another controller can commission a node.</summary>
/// <param name="SetupPinCode">The setup PIN code.</param>
/// <param name="ManualCode">The manual pairing code.</param>
/// <param name="QrCode">The QR code payload.</param>
public sealed record MatterCommissioningWindow(uint SetupPinCode, string ManualCode, string QrCode);
