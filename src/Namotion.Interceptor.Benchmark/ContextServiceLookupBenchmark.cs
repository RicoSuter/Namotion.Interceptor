using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures a cached <see cref="IInterceptorSubjectContext.GetServices{TInterface}"/> lookup through the interface on a
/// subject's context, which the validation interceptor runs on every property write.
/// </summary>
[MemoryDiagnoser]
public class ContextServiceLookupBenchmark
{
    private const int Lookups = 64;

    private IInterceptorSubjectContext _subjectContext;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithDataAnnotationValidation();

        _subjectContext = ((IInterceptorSubject)new Car(context)).Context;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public int GetServicesOnSubjectContext()
    {
        var context = _subjectContext;
        var count = 0;
        for (var index = 0; index < Lookups; index++)
        {
            count += context.GetServices<IPropertyValidator>().Length;
        }

        return count;
    }
}
