using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor.Tests;

[InterceptorSubject]
public partial class ChartTargetSubject
{
    [State(Title = "Water temperature", Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State]
    public partial bool IsOn { get; set; }

    [State(IsCumulative = true)]
    public partial double Energy { get; set; }

    public partial int NotRecorded { get; set; }

    public partial ChartTargetSubject? Child { get; set; }

    public partial HistoryChart? Chart { get; set; }
}
