using System.Linq;
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
    
    [Params(
        "regular",
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
            .Select(i => new Car())
            .ToArray();
    }

    // [Benchmark]
    public void IncrementDerivedAverage()
    {
        _object.Tires[0].Pressure += 5;
        _object.Tires[1].Pressure += 6;
        _object.Tires[2].Pressure += 7;
        _object.Tires[3].Pressure += 8;

        var average = _object.AveragePressure;

        _object.PreviousCars = null;
    }

    // [Benchmark]
    public void Write()
    {
        _object.Tires[0].Pressure = 5;
        _object.Tires[1].Pressure = 6;
        _object.Tires[2].Pressure = 7;
        _object.Tires[3].Pressure = 8;
    }

    // [Benchmark]
    public decimal Read()
    {
        return 
            _object.Tires[0].Pressure +
            _object.Tires[1].Pressure +
            _object.Tires[2].Pressure +
            _object.Tires[3].Pressure;
    }

    // [Benchmark]
    public void DerivedAverage()
    {
        var average = _object.AveragePressure;
    }

    // [Benchmark]
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
}