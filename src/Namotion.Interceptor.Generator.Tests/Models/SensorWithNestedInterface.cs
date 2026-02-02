using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class SensorWithNestedInterface : OuterClass.INestedSensor
{
    public partial double Value { get; set; }
}
