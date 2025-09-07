using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

[InterceptorSubject]
public partial class Car
{
    public partial int Speed { get; set; }
}