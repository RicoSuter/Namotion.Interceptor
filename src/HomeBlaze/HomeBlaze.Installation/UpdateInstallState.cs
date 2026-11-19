namespace HomeBlaze.Installation;

/// <summary>
/// The progress of an update installed through the updater sidecar.
/// </summary>
public enum UpdateInstallState
{
    /// <summary>No install since the start.</summary>
    None,

    /// <summary>The request is written and waits for the updater.</summary>
    Requested,

    /// <summary>The updater pulls the image and recreates the container.</summary>
    Installing,

    /// <summary>The installation runs a different build than the one that requested the install.</summary>
    Succeeded,

    /// <summary>The updater finished without a newer image for the configured tag.</summary>
    UpToDate,

    /// <summary>Compose failed or the updater did not answer in time.</summary>
    Failed
}
