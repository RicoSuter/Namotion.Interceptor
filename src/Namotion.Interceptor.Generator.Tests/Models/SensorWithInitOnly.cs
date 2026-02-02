using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class SensorWithInitOnly : IInitOnlyInterface
{
    public partial string Id { get; init; }
}
