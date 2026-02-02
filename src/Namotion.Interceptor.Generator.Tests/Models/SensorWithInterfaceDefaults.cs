using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class SensorWithInterfaceDefaults : ITemperatureSensorInterface
{
    public partial double TemperatureCelsius { get; set; }
}
