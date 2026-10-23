using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class CommonValueTypes
{
    public partial int IntValue { get; set; }

    public partial long LongValue { get; set; }

    public partial double DoubleValue { get; set; }

    public partial float FloatValue { get; set; }

    public partial decimal DecimalValue { get; set; }

    public partial bool BoolValue { get; set; }

    public partial DateTime DateTimeValue { get; set; }

    public partial DateTimeOffset DateTimeOffsetValue { get; set; }

    public partial TimeSpan TimeSpanValue { get; set; }

    public partial Guid GuidValue { get; set; }
}
