using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Automation.Tests.Testing;

[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
}

/// <summary>A device whose readings are null until set, with a secret and a value that expressions never show.</summary>
[InterceptorSubject]
public partial class TestSensor
{
    [State]
    public partial double? Gust { get; set; }

    [State]
    public partial double? Rain { get; set; }

    [State]
    public partial double? Power { get; set; }

    [State]
    public partial string? Name { get; set; }

    [State]
    public partial DoorState? Door { get; set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? ApiKey { get; set; }

    [State]
    [ExcludeFromExpressions]
    public partial string? RawPayload { get; set; }
}

/// <summary>A parent of rules with a state entry time, standing in for an automation.</summary>
[InterceptorSubject]
public partial class TestRuleHost
{
    public TestRuleHost()
    {
        Rules = new Dictionary<string, AutomationRule>();
    }

    [State]
    public partial DateTimeOffset? EnteredAt { get; set; }

    public partial Dictionary<string, AutomationRule> Rules { get; set; }
}

public sealed record TestPressEvent : DeviceEvent
{
    public required string Gesture { get; init; }
}
