using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

[InterceptorSubject]
public partial class MeasuredSubject
{
    public MeasuredSubject()
    {
        TargetSpeed_Maximum = 3000;
    }

    [State]
    public partial int Speed { get; set; }

    [State]
    public partial int? Pressure { get; set; }

    [State(Unit = StateUnit.Percent)]
    public partial double Load { get; set; }

    [State]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), KnownAttributes.Minimum)]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), KnownAttributes.Maximum)]
    public partial int TargetSpeed_Maximum { get; set; }
}
