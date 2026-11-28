namespace Namotion.Matter;

/// <summary>The clock a <see cref="MatterNodeEventReceivedEvent.Timestamp"/> is on.</summary>
public enum MatterEventTimestampType
{
    /// <summary>Milliseconds since the device started.</summary>
    DeviceUptime = 0,

    /// <summary>Milliseconds since the Unix epoch, on the device's clock.</summary>
    Epoch = 1,

    /// <summary>Milliseconds since the Unix epoch, on the server's clock, because the device sent no timestamp.</summary>
    ServerTime = 2
}
