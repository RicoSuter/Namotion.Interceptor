using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class MethodSubject
{
    public partial string Name { get; set; }

    public partial MethodSubject[]? Children { get; set; }

    public partial int Counter { get; set; }

    [SubjectMethod]
    private int MethodWithoutInterceptor(int value) => value + 1;

    [SubjectMethod]
    private int IncrementCounterWithoutInterceptor(int delta)
    {
        Counter += delta;
        return Counter;
    }
}
