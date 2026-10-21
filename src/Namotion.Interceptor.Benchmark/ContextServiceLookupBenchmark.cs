using System.Collections.Immutable;
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

    [Benchmark]
    public ImmutableArray<IPropertyValidator> GetServicesOnSubjectContext()
    {
        return _subjectContext.GetServices<IPropertyValidator>();
    }
}
