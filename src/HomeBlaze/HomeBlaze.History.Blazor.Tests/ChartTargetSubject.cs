using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor.Tests;

public enum ChartTargetMode
{
    Eco,
    Boost
}

[InterceptorSubject]
public partial class ChartTargetSubject
{
    [State(Title = "Water temperature", Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State]
    public partial bool IsOn { get; set; }

    [State(IsCumulative = true)]
    public partial double Energy { get; set; }

    [State]
    public partial string? Status { get; set; }

    [State]
    public partial ChartTargetMode Mode { get; set; }

    [State(IsDiscrete = true)]
    public partial int Level { get; set; }

    public partial int NotRecorded { get; set; }

    public partial ChartTargetSubject? Child { get; set; }

    public partial HistoryChart? Chart { get; set; }

    public partial ChartPageSubject? Page { get; set; }
}

/// <summary>A path scope like a page, from which the relative paths of the charts inside it resolve.</summary>
[InterceptorSubject]
public partial class ChartPageSubject : IPathScope
{
    public partial HistoryChart? Chart { get; set; }

    public IInterceptorSubject? TryGetNamedSubject(string name) => null;
}
