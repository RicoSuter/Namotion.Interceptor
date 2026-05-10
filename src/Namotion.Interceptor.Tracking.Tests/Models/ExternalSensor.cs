using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ExternalSensor
{
    public Func<double> ExternalValueProvider { get; set; } = () => 0.0;

    public partial string? Label { get; set; }

    [Derived]
    public double CalibratedTemperature => ExternalValueProvider();
}
