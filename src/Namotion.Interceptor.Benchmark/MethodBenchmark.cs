using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class MethodBenchmark
{
    private MethodSubject _plainSubject;
    private MethodSubject _interceptedSubject;
    private MethodSubject _transactionalSubject;

    private RegisteredSubjectMethod _plainRegisteredMethod;
    private RegisteredSubjectMethod _interceptedRegisteredMethod;

    private IInterceptorSubjectContext _transactionalContext;

    private readonly object?[] _arguments = [42];

    [GlobalSetup]
    public void Setup()
    {
        var plainContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _plainSubject = new MethodSubject(plainContext);
        _plainRegisteredMethod = _plainSubject.TryGetRegisteredSubject()!.TryGetMethod("Method")!;

        var interceptedContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<IMethodInterceptor>(() => new NoOpMethodInterceptor());

        _interceptedSubject = new MethodSubject(interceptedContext);
        _interceptedRegisteredMethod = _interceptedSubject.TryGetRegisteredSubject()!.TryGetMethod("Method")!;

        _transactionalContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();

        _transactionalSubject = new MethodSubject(_transactionalContext);
    }

    [Benchmark]
    public int InvokeDirect_NoInterceptor()
    {
        return _plainSubject.Method(42);
    }

    [Benchmark]
    public int InvokeDirect_WithInterceptor()
    {
        return _interceptedSubject.Method(42);
    }

    [Benchmark]
    public object? InvokeViaRegistry_NoInterceptor()
    {
        return _plainRegisteredMethod.Invoke(_arguments);
    }

    [Benchmark]
    public object? InvokeViaRegistry_WithInterceptor()
    {
        return _interceptedRegisteredMethod.Invoke(_arguments);
    }

    [Benchmark]
    public int InvokeDirect_WritesProperty()
    {
        return _plainSubject.IncrementCounter(1);
    }

    [Benchmark]
    public async Task<int> InvokeDirect_WritesProperty_WithTransaction()
    {
        using var transaction = await _transactionalContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var result = _transactionalSubject.IncrementCounter(1);
        await transaction.CommitAsync(default);
        return result;
    }

    [Benchmark]
    public void AddLotsOfMethodSubjects()
    {
        _plainSubject.Children = Enumerable.Range(0, 1000)
            .Select(_ => new MethodSubject())
            .ToArray();
    }

    private sealed class NoOpMethodInterceptor : IMethodInterceptor
    {
        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
            => next(ref context);
    }
}
