using TwinCAT.Ads;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Classifies ADS errors as transient (retry) or permanent (don't retry).
/// Unknown error codes are treated as transient (safer default).
/// </summary>
internal static class AdsErrorClassifier
{
    private static readonly HashSet<AdsErrorCode> PermanentErrors =
    [
        AdsErrorCode.DeviceSymbolNotFound,
        AdsErrorCode.DeviceInvalidSize,
        AdsErrorCode.DeviceInvalidData,
        AdsErrorCode.DeviceServiceNotSupported,
        AdsErrorCode.DeviceInvalidAccess,
        AdsErrorCode.DeviceInvalidOffset,
    ];

    /// <summary>
    /// Determines if an ADS error is transient and should be retried.
    /// </summary>
    /// <param name="errorCode">The ADS error code to classify.</param>
    /// <returns>True if the error is transient and should be retried; false if permanent.</returns>
    public static bool IsTransientError(AdsErrorCode errorCode)
    {
        return !PermanentErrors.Contains(errorCode);
    }
}
