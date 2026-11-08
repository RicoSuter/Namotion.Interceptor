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

    [State]
    public partial object? Reading { get; set; }
}

/// <summary>A reading whose text cannot be shown, because a getter that its text reads fails, as a faulty device value does.</summary>
public sealed record UnreadableReading
{
    public string Raw => Decode();

    private static string Decode() => throw new InvalidOperationException("The reading is corrupt");
}

/// <summary>A subject that holds a replaceable sensor.</summary>
[InterceptorSubject]
public partial class TestSensorHolder
{
    public partial TestSensor? Sensor { get; set; }
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

/// <summary>A room whose devices are held by key, by position, by reference and as an event source.</summary>
[InterceptorSubject]
public partial class TestRoom
{
    public TestRoom()
    {
        Devices = new Dictionary<string, TestSensor>();
        Items = [];
    }

    [State]
    public partial int Level { get; set; }

    public partial Dictionary<string, TestSensor> Devices { get; set; }

    public partial List<TestSensor> Items { get; set; }

    public partial TestSensor? Sensor { get; set; }

    public partial TestButton? Button { get; set; }
}

/// <summary>A folder whose children are addressed by their key alone, as storage folders are.</summary>
[InterceptorSubject]
public partial class TestFolder
{
    public TestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [State]
    public partial int Level { get; set; }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public void Add(string name, IInterceptorSubject subject) =>
        Children = new Dictionary<string, IInterceptorSubject>(Children) { [name] = subject };

    public void Remove(string name)
    {
        var children = new Dictionary<string, IInterceptorSubject>(Children);
        children.Remove(name);
        Children = children;
    }
}

/// <summary>A holder whose operation replaces its sensor, a structural change on the route of a rule that reads the sensor.</summary>
[InterceptorSubject]
public partial class TestReplacer
{
    private int _replacements;

    public partial TestSensor? Sensor { get; set; }

    public int Replacements => Volatile.Read(ref _replacements);

    /// <summary>Replaces the sensor by one whose gust is high when the current one is low, and low otherwise.</summary>
    [Operation]
    public void Replace()
    {
        Interlocked.Increment(ref _replacements);
        Sensor = new TestSensor { Gust = Sensor?.Gust > 8 ? 1 : 9 };
    }
}
