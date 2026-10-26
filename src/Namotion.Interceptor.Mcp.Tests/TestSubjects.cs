using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Mcp.Tests;

[InterceptorSubject]
public partial class TestRoom
{
    public partial string Name { get; set; }
    public partial decimal Temperature { get; set; }
    public partial TestDevice? Device { get; set; }
}

[InterceptorSubject]
public partial class TestDevice
{
    public partial string DeviceName { get; set; }
    public partial bool IsOn { get; set; }
}

[InterceptorSubject]
public partial class TestContainer
{
    public partial string Name { get; set; }
    public partial Dictionary<string, TestContainer> Children { get; set; }

    public TestContainer()
    {
        Children = new Dictionary<string, TestContainer>();
    }
}

[InterceptorSubject]
public partial class TestSensor
{
    public partial decimal Reading { get; internal set; }
}

[InterceptorSubject]
public partial class TestSchedule
{
    public partial TimeSpan Interval { get; set; }
    public partial DateTime CreatedAt { get; set; }
    public partial DateTimeOffset StartsAt { get; set; }
    public partial Guid Id { get; set; }
    public partial DayOfWeek Day { get; set; }
    public partial DayOfWeek? OptionalDay { get; set; }
}

[InterceptorSubject]
public partial class TestNumbers
{
    public partial int IntValue { get; set; }
    public partial long LongValue { get; set; }
    public partial float FloatValue { get; set; }
    public partial double DoubleValue { get; set; }
}

[InterceptorSubject]
public partial class TestDeviceGroup
{
    public partial Dictionary<string, TestDevice> Devices { get; set; }

    public TestDeviceGroup()
    {
        Devices = new Dictionary<string, TestDevice>();
    }
}

[InterceptorSubject]
public partial class TestMixedGroup
{
    public partial Dictionary<string, IInterceptorSubject> Items { get; set; }

    public TestMixedGroup()
    {
        Items = new Dictionary<string, IInterceptorSubject>();
    }
}

[InterceptorSubject]
public partial class TestHouse
{
    public partial string Address { get; set; }
    public partial TestDevice? Private { get; set; }
    public partial TestDevice? Public { get; set; }
}

[InterceptorSubject]
public partial class TestFolder
{
    public partial string Name { get; set; }
    public partial string Secret { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestFolder> Children { get; set; }

    public TestFolder()
    {
        Children = new Dictionary<string, TestFolder>();
    }
}

[InterceptorSubject]
public partial class TestNode
{
    public partial string Name { get; set; }
    public partial TestNode? Secret { get; set; }
    public partial TestNode? A { get; set; }
    public partial TestNode? B { get; set; }
    public partial TestNode? Child { get; set; }
}
