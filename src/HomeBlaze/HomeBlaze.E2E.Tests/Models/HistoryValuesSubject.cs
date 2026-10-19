using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject whose recorded values a test sets one by one, so that a history chart shows values that no device of the
/// application produces on demand: a constant, values around zero, very large or very small ranges and gaps.
/// </summary>
[InterceptorSubject]
public partial class HistoryValuesSubject : IConfigurable
{
    [State]
    public partial double? Constant { get; set; }

    [State]
    public partial double? Single { get; set; }

    [State]
    public partial double? CrossingZero { get; set; }

    [State]
    public partial double? Negative { get; set; }

    [State]
    public partial double? Large { get; set; }

    [State]
    public partial double? Huge { get; set; }

    [State]
    public partial double? Small { get; set; }

    [State]
    public partial double? Decimal { get; set; }

    [State]
    public partial double? Gapped { get; set; }

    [State]
    public partial double? Fraction { get; set; }

    [State(Unit = StateUnit.Percent)]
    public partial double? Percent { get; set; }

    [State]
    public partial bool IsOn { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
