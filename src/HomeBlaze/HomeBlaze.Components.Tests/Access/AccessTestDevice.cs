using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Access;

public enum AccessTestMode
{
    Off,
    Eco
}

/// <summary>
/// The root of a widget test graph: one member of each kind a widget binds to, any of which a test refuses to the
/// viewer, and the widget under test.
/// </summary>
[InterceptorSubject]
public partial class AccessTestDevice
{
    public AccessTestDevice()
    {
        Devices = [];
    }

    [State]
    public partial string? Text { get; set; }

    [State]
    public partial bool Flag { get; set; }

    [State]
    public partial AccessTestMode Mode { get; set; }

    [State(Unit = StateUnit.Percent)]
    public partial double Level { get; set; }

    [Configuration]
    public partial bool ConfiguredFlag { get; set; }

    [Configuration]
    public partial AccessTestMode ConfiguredMode { get; set; }

    [Configuration]
    [State(Unit = StateUnit.Percent)]
    public partial double ConfiguredLevel { get; set; }

    /// <summary>A door lock behind a reference.</summary>
    public partial AccessTestLock? Lock { get; set; }

    /// <summary>The subjects a table lists.</summary>
    public partial AccessTestRow[] Devices { get; set; }

    public partial IInterceptorSubject? Widget { get; set; }

    [Operation]
    public void Stop()
    {
    }
}

[InterceptorSubject]
public partial class AccessTestLock : IDoorLockState, IDoorSensor, ITitleProvider
{
    public partial DoorLockState? LockState { get; set; }

    public partial DoorState? DoorState { get; set; }

    public string? Title => "Front door";
}

[InterceptorSubject]
public partial class AccessTestRow : ITitleProvider
{
    public AccessTestRow()
    {
        Name = string.Empty;
    }

    [State]
    public partial string Name { get; set; }

    [State]
    public partial string? Secret { get; set; }

    public string? Title => Name;
}
