using Namotion.Interceptor.Attributes;

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
