namespace Namotion.Matter;

/// <summary>A controller request failed, or a device rejected it.</summary>
public sealed class MatterException : Exception
{
    /// <summary>Initializes a new instance with an unknown error code.</summary>
    public MatterException()
    {
    }

    /// <summary>Initializes a new instance with an unknown error code.</summary>
    /// <param name="message">The message.</param>
    public MatterException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with an unknown error code.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public MatterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The message.</param>
    /// <param name="status">The Matter status the device answered with, if any.</param>
    /// <param name="clusterStatus">The cluster-specific status the device answered with, if any.</param>
    /// <param name="commissioningFailureReason">Why commissioning failed, for a failed commissioning.</param>
    public MatterException(
        MatterErrorCode errorCode,
        string message,
        int? status = null,
        int? clusterStatus = null,
        MatterCommissioningFailureReason? commissioningFailureReason = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Status = status;
        ClusterStatus = clusterStatus;
        CommissioningFailureReason = commissioningFailureReason;
    }

    /// <summary>Gets the error code.</summary>
    public MatterErrorCode ErrorCode { get; }

    /// <summary>Gets the Matter status the device answered with, or null when no device answered.</summary>
    public int? Status { get; }

    /// <summary>Gets the cluster-specific status the device answered with, or null when it sent none.</summary>
    public int? ClusterStatus { get; }

    /// <summary>Gets why commissioning failed, or null when the request was not a commissioning.</summary>
    public MatterCommissioningFailureReason? CommissioningFailureReason { get; }
}
