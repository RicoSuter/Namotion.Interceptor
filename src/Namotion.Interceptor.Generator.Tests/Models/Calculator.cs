using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class Calculator
{
    public int InternalResult { get; private set; }
    
    protected void ExecuteWithoutInterceptor()
    {
        InternalResult = 42;
    }
        
    protected void ExecuteParamsWithoutInterceptor(int a, int b)
    {
        InternalResult = a + b;
    }
        
    protected int SumWithoutInterceptor(int a, int b)
    {
        return a + b;
    }
}