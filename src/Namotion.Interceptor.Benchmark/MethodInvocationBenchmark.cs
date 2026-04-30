using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class MethodInvocationBenchmark
{
    private MethodSubject _subject;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        _subject = new MethodSubject(context);
    }

    [Benchmark]
    public int InvokeMethodWithInterception()
    {
        return _subject.Method(42);
    }
}
