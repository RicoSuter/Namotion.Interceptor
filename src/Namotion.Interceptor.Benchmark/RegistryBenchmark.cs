using System;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class RegistryBenchmark
{
    private Car _object;
    private IInterceptorSubjectContext? _context;
    private int _writeCounter;
    
    [Params(
        // "regular",
        "interceptor"
    )]
    public string? Type;

    [GlobalSetup]
    public void Setup()
    {
        switch (Type)
        {
            case "regular":
                _object = new Car();
                break;
            
            case "interceptor":
                _context = InterceptorSubjectContext
                    .Create()
                    .WithFullPropertyTracking()
                    .WithRegistry();

                _object = new Car(_context);
                AddLotsOfPreviousCars();
                break;
        }
    }

    [Benchmark]
    public void AddLotsOfPreviousCars()
    {
        _object.PreviousCars = Enumerable.Range(0, 1000)
            .Select(_ => new Car())
            .ToArray();
    }

    [Benchmark]
    public void IncrementDerivedAverage()
    {
        _object.Tires[0].Pressure += 5;
        _object.Tires[1].Pressure += 6;
        _object.Tires[2].Pressure += 7;
        _object.Tires[3].Pressure += 8;

        var average = _object.AveragePressure;

        _object.PreviousCars = null;
    }

    /// <summary>
    /// No-op write fast path: writes the same constant values every iteration. After warmup the
    /// equality-check interceptor short-circuits each write, so the terminal store, derived
    /// cascade, and any UtcNow snap never run. Measures interceptor-chain overhead for writes
    /// whose new value equals the current value, which is common when a connector republishes
    /// unchanged data.
    /// </summary>
    [Benchmark]
    public void WriteNoOp()
    {
        _object.Tires[0].Pressure = 5;
        _object.Tires[1].Pressure = 6;
        _object.Tires[2].Pressure = 7;
        _object.Tires[3].Pressure = 8;
    }

    /// <summary>
    /// Real write: values change every iteration so the equality check passes, the terminal
    /// store runs, and the write timestamp is snapped. Writes to <c>Pressure_Minimum</c>
    /// (no derived dependents) to isolate the leaf-write cost from cascade work.
    /// </summary>
    [Benchmark]
    public void Write()
    {
        var v = (decimal)Interlocked.Increment(ref _writeCounter);
        _object.Tires[0].Pressure_Minimum = v;
        _object.Tires[1].Pressure_Minimum = v + 1;
        _object.Tires[2].Pressure_Minimum = v + 2;
        _object.Tires[3].Pressure_Minimum = v + 3;
    }

    /// <summary>
    /// Real write under an active <see cref="SubjectChangeContext.WithChangedTimestamp(System.DateTimeOffset?)"/>
    /// scope: the connector-import path, where each imported value carries a source timestamp.
    /// Exercises the scope-ticks branch of the write-timestamp pipeline.
    /// </summary>
    [Benchmark]
    public void WriteWithTimestampScope()
    {
        var v = (decimal)Interlocked.Increment(ref _writeCounter);
        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            _object.Tires[0].Pressure_Minimum = v;
            _object.Tires[1].Pressure_Minimum = v + 1;
            _object.Tires[2].Pressure_Minimum = v + 2;
            _object.Tires[3].Pressure_Minimum = v + 3;
        }
    }

    [Benchmark]
    public decimal Read()
    {
        return 
            _object.Tires[0].Pressure +
            _object.Tires[1].Pressure +
            _object.Tires[2].Pressure +
            _object.Tires[3].Pressure;
    }

    [Benchmark]
    public void DerivedAverage()
    {
        var average = _object.AveragePressure;
    }

    [Benchmark]
    public void ChangeAllTires()
    {
        var newTires = new Tire[]
        {
            new Tire(),
            new Tire(),
            new Tire(),
            new Tire()
        };

        _object.Tires = newTires;
    }

    [Benchmark]
    public string GetOrAddSubjectId()
    {
        return _object.GetOrAddSubjectId();
    }

    [Benchmark]
    public string GenerateSubjectId()
    {
        return SubjectRegistryExtensions.GenerateSubjectId();
    }
}
