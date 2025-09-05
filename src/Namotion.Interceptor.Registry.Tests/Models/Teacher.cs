using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

[InterceptorSubject]
public partial class Teacher : Person
{
    public partial string MainCourse { get; set; }
}