using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A subject with a property or a method of every kind that a control widget can target.
/// </summary>
[InterceptorSubject]
public partial class ControlTargetSubject
{
    public ControlTargetSubject()
    {
        Speed_Minimum = 0;
        Speed_Maximum = 3000;
    }

    public partial bool IsOn { get; set; }

    public partial bool? IsOpen { get; set; }

    public partial bool IsReady { get; internal set; }

    public partial ControlTargetMode Mode { get; set; }

    public partial ControlTargetMode? OptionalMode { get; set; }

    public partial ControlTargetMode FixedMode { get; internal set; }

    public partial ControlTargetFeatures Features { get; set; }

    public partial int Speed { get; set; }

    [PropertyAttribute(nameof(Speed), KnownAttributes.Minimum)]
    public partial int Speed_Minimum { get; set; }

    [PropertyAttribute(nameof(Speed), KnownAttributes.Maximum)]
    public partial int Speed_Maximum { get; set; }

    public partial double Temperature { get; set; }

    [State(Unit = StateUnit.Percent)]
    public partial double Load { get; set; }

    public partial string? Name { get; set; }

    [Operation]
    public void Start()
    {
    }

    [Query]
    public int Count() => 1;
}

public enum ControlTargetMode
{
    Eco,
    Normal,
    Boost
}

[Flags]
public enum ControlTargetFeatures
{
    None = 0,
    Light = 1,
    Heat = 2
}
