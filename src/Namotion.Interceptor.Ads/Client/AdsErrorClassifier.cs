using TwinCAT.Ads;

namespace Namotion.Interceptor.Ads.Client;

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

    /// <summary>
    /// Reads the ADS error code off an exception.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Exception.HResult"/>: that carries the generic managed 0x80131500 for every
    /// ADS error, so casting it to <see cref="AdsErrorCode"/> yields -2146233088 and matches nothing.
    /// The code is only on <see cref="AdsErrorException"/>.
    /// </remarks>
    /// <param name="exception">The exception to read the code from.</param>
    /// <returns>The ADS error code, or <see cref="AdsErrorCode.NoError"/> when the exception carries
    /// none. That falls through to the transient default, which is the safer way to be wrong.</returns>
    public static AdsErrorCode GetErrorCode(Exception exception)
    {
        return exception is AdsErrorException adsErrorException
            ? adsErrorException.ErrorCode
            : AdsErrorCode.NoError;
    }
}
