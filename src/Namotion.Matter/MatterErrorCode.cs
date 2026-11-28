namespace Namotion.Matter;

/// <summary>The error codes of a failed controller request.</summary>
public enum MatterErrorCode
{
    /// <summary>An unknown error, also a status a device answered a write with.</summary>
    Unknown = 0,

    /// <summary>Commissioning failed.</summary>
    NodeCommissionFailed = 1,

    /// <summary>Interviewing the node failed.</summary>
    NodeInterviewFailed = 2,

    /// <summary>The node is offline or not interviewed yet.</summary>
    NodeNotReady = 3,

    /// <summary>No session could be established with the node.</summary>
    NodeNotResolving = 4,

    /// <summary>The node does not exist.</summary>
    NodeNotExists = 5,

    /// <summary>The client and the server versions do not match.</summary>
    VersionMismatch = 6,

    /// <summary>The Matter stack failed.</summary>
    SdkStackError = 7,

    /// <summary>The request's arguments are invalid.</summary>
    InvalidArguments = 8,

    /// <summary>The command is unknown.</summary>
    InvalidCommand = 9,

    /// <summary>Checking for a firmware update failed.</summary>
    UpdateCheckError = 10,

    /// <summary>A firmware update failed.</summary>
    UpdateError = 11,

    /// <summary>The node refused a check-in registration because other administrators may not support it.</summary>
    IcdMultiAdmin = 100,

    /// <summary>Uploading a firmware image failed.</summary>
    OtaUploadError = 101,

    /// <summary>The camera, the offer or the request rules the stream out.</summary>
    CameraStreamIncompatible = 102,

    /// <summary>The camera has no capacity left for the stream.</summary>
    CameraResourceExhausted = 103,

    /// <summary>The camera stream is still in use.</summary>
    CameraStreamInUse = 104,

    /// <summary>The endpoint lacks a cluster the camera command needs.</summary>
    CameraNotSupported = 105,

    /// <summary>The camera's privacy mode forbids the request.</summary>
    CameraPrivacyMode = 106
}
