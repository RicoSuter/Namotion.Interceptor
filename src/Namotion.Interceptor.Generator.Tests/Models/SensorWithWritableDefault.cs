using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class SensorWithWritableDefault : IWritableDefaultInterface
{
    public partial double Temperature { get; set; }
}
