namespace Namotion.Matter;

/// <summary>Why commissioning failed.</summary>
public enum MatterCommissioningFailureReason
{
    /// <summary>A failure the server does not classify.</summary>
    Other,

    /// <summary>No matching device was found.</summary>
    NotDiscovered,

    /// <summary>The device rejected the pairing code.</summary>
    PasscodeMismatch,

    /// <summary>The pairing code could not be decoded.</summary>
    InvalidPayload,

    /// <summary>The device certificates were not accepted.</summary>
    AttestationRejected,

    /// <summary>The device was found but not reached in time.</summary>
    Timeout
}
