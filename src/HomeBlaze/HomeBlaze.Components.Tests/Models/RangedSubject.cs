using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

[InterceptorSubject]
public partial class RangedSubject
{
    public RangedSubject()
    {
        Speed_Minimum = 0;
        Speed_Maximum = 3000;
        Level_Minimum = "low";
        Level_Maximum = 10m;
    }

    [State]
    public partial int Speed { get; set; }

    [PropertyAttribute(nameof(Speed), KnownAttributes.Minimum)]
    public partial int Speed_Minimum { get; set; }

    [PropertyAttribute(nameof(Speed), KnownAttributes.Maximum)]
    public partial int Speed_Maximum { get; set; }

    [State]
    public partial double Level { get; set; }

    [PropertyAttribute(nameof(Level), KnownAttributes.Minimum)]
    public partial string Level_Minimum { get; set; }

    [PropertyAttribute(nameof(Level), KnownAttributes.Maximum)]
    public partial decimal Level_Maximum { get; set; }

    [State(Unit = StateUnit.Percent)]
    public partial double Load { get; set; }

    [State]
    public partial double Plain { get; set; }
}
