using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class MethodSubject
{
    public partial string Name { get; set; }

    public partial MethodSubject[]? Children { get; set; }

    [SubjectMethod]
    private int MethodWithoutInterceptor(int value) => value + 1;
}
