using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures the intercepted read and write fast path as a function of delegation-chain depth.
/// Every attached child subject inherits the context of its parent as its only fallback context,
/// so a subject graph of depth N resolves every property access through a chain of N delegating
/// contexts. The other benchmarks only cover graphs 2 to 3 levels deep, so this is the one that
/// sees the cost of chain resolution. <see cref="Depth"/> counts the proxy contexts between the
/// subject and the context holding the services; the subject's own executor adds one more hop.
/// </summary>
[MemoryDiagnoser]
public class DelegationDepthBenchmark
{
    private Tire _tire;
    private int _writeCounter;

    [Params(1, 8, 64)]
    public int Depth;

    [GlobalSetup]
    public void Setup()
    {
        IInterceptorSubjectContext context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        for (var index = 0; index < Depth; index++)
        {
            var proxy = InterceptorSubjectContext.Create();
            proxy.AddFallbackContext(context);
            context = proxy;
        }

        _tire = new Tire(context);
    }

    [Benchmark]
    public void Write()
    {
        var value = (decimal)Interlocked.Increment(ref _writeCounter);
        _tire.Pressure = value;
        _tire.Pressure_Minimum = value + 1;
        _tire.Pressure_Maximum = value + 2;
    }

    [Benchmark]
    public decimal Read()
    {
        return _tire.Pressure + _tire.Pressure_Minimum + _tire.Pressure_Maximum;
    }
}
