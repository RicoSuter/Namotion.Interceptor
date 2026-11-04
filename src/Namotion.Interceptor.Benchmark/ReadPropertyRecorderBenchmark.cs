using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures an intercepted property read with and without <see cref="ReadPropertyRecorder"/> registered, in each
/// state of its async-local scopes. Each state runs in its own process with a targeted <see cref="GlobalSetupAttribute"/>,
/// because a scope started on the benchmark thread leaves its list in the execution context for good.
/// Uses only API that exists on both sides of a branch comparison.
/// </summary>
[MemoryDiagnoser]
public class ReadPropertyRecorderBenchmark
{
    private const int Reads = 256;

    private Car _car;
    private ReadPropertyRecorderScope? _scope;

    [GlobalSetup(Target = nameof(ReadWithoutRecorder))]
    public void SetupWithoutRecorder()
    {
        _car = new Car(InterceptorSubjectContext.Create().WithFullPropertyTracking());
    }

    // The recorder is registered but no scope was ever started on this execution context.
    [GlobalSetup(Target = nameof(ReadWithRecorderIdle))]
    public void SetupRecorderIdle()
    {
        _car = CreateCarWithRecorder();
    }

    // A scope was started and disposed, which leaves an empty scope list in the execution context.
    [GlobalSetup(Target = nameof(ReadWithRecorderAfterScopeDisposed))]
    public void SetupRecorderAfterScopeDisposed()
    {
        _car = CreateCarWithRecorder();
        ReadPropertyRecorder.Start().Dispose();
    }

    // One scope records, and the property is already recorded, so every read is a dictionary hit.
    [GlobalSetup(Target = nameof(ReadWithScopeActive))]
    public void SetupScopeActive()
    {
        _car = CreateCarWithRecorder();
        _scope = ReadPropertyRecorder.Start();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Reads)]
    public int ReadWithoutRecorder() => ReadName();

    [Benchmark(OperationsPerInvoke = Reads)]
    public int ReadWithRecorderIdle() => ReadName();

    [Benchmark(OperationsPerInvoke = Reads)]
    public int ReadWithRecorderAfterScopeDisposed() => ReadName();

    [Benchmark(OperationsPerInvoke = Reads)]
    public int ReadWithScopeActive() => ReadName();

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_scope is not null && _scope.GetPropertiesAndDispose().Count != 1)
        {
            throw new InvalidOperationException("The scope did not record the reads of the benchmark thread.");
        }
    }

    private int ReadName()
    {
        var car = _car;
        var sum = 0;
        for (var i = 0; i < Reads; i++)
        {
            sum += car.Name.Length;
        }

        return sum;
    }

    private static Car CreateCarWithRecorder()
    {
        return new Car(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder());
    }
}
