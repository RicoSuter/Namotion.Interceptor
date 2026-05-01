using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

[InterceptorSubject]
public partial class Calculator
{
    public partial int Value { get; set; }

    [SubjectMethod]
    public int Add(int a, int b) => a + b;

    [SubjectMethod]
    public void Reset() { Value = 0; }
}
