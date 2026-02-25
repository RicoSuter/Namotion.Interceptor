using TwinCAT.Ads;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Classifies ADS errors as transient (retry) or permanent (don't retry).
/// </summary>
internal static class AdsErrorClassifier
{
    /// <summary>
    /// Determines if an ADS error is transient and should be retried.
    /// </summary>
    /// <param name="errorCode">The ADS error code to classify.</param>
    /// <returns>True if the error is transient and should be retried; false if permanent.</returns>
    public static bool IsTransientError(AdsErrorCode errorCode)
    {
        return errorCode switch
        {
            // Permanent errors - don't retry
            AdsErrorCode.DeviceSymbolNotFound => false,
            AdsErrorCode.DeviceInvalidSize => false,
            AdsErrorCode.DeviceInvalidData => false,
            AdsErrorCode.DeviceServiceNotSupported => false,
            AdsErrorCode.DeviceInvalidAccess => false,
            AdsErrorCode.DeviceInvalidOffset => false,

            // Transient errors - retry
            AdsErrorCode.TargetPortNotFound => true,
            AdsErrorCode.TargetMachineNotFound => true,
            AdsErrorCode.ClientPortNotOpen => true,
            AdsErrorCode.DeviceError => true,
            AdsErrorCode.DeviceTimeOut => true,
            AdsErrorCode.DeviceBusy => true,

            // Default: treat unknown as transient (safer)
            _ => true
        };
    }
}
