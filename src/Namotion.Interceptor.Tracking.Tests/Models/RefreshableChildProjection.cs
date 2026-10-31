using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>A derived projection of a stored child, recalculated on demand by a scalar refresh counter.</summary>
[InterceptorSubject]
public partial class RefreshableChildProjection
{
    public partial Person? Child { get; set; }
    public partial int Refresh { get; set; }

    [Derived]
    public Person? Projected
    {
        get
        {
            _ = Refresh;
            return Child;
        }
    }
}
