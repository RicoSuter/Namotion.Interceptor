using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

public enum PathTargetMode
{
    Eco,
    Normal
}

[InterceptorSubject]
public partial class PathTargetSubject
{
    public partial bool IsOn { get; set; }

    public partial bool? IsOpen { get; set; }

    public partial int Speed { get; set; }

    [PropertyAttribute(nameof(Speed), KnownAttributes.Minimum)]
    public partial int Speed_Minimum { get; set; }

    public partial int? Limit { get; set; }

    public partial long Count { get; set; }

    public partial double Temperature { get; set; }

    public partial decimal Price { get; set; }

    public partial PathTargetMode Mode { get; set; }

    public partial PathTargetMode? FallbackMode { get; set; }

    public partial string? Name { get; set; }

    public partial char Initial { get; set; }

    public partial DateTimeOffset LastSeen { get; set; }

    public partial int Reading { get; internal set; }

    [Derived]
    public int DoubledSpeed => Speed * 2;

    [Operation]
    public void Stop()
    {
    }

    [Derived]
    [PropertyAttribute(nameof(Stop), KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Speed > 0;

    [Query]
    public Task<string> GetStatusAsync() => Task.FromResult("Running");
}
