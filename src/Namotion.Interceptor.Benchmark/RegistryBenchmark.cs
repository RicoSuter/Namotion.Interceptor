using System.Linq;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class RegistryBenchmark
{
    private IInterceptorSubjectContext? _context;

    private Car _object;

    private MethodSubject _methodSubject;
    private RegisteredSubjectMethod _registeredMethod;
    private readonly object?[] _methodArguments = [42];

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
                _methodSubject = new MethodSubject();
                break;

            case "interceptor":
                _context = InterceptorSubjectContext
                    .Create()
                    .WithFullPropertyTracking()
                    .WithRegistry();

                _object = new Car(_context);
                AddLotsOfPreviousCars();

                _methodSubject = new MethodSubject(_context);
                _registeredMethod = _methodSubject.TryGetRegisteredSubject()!.TryGetMethod("Method")!;
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

    [Benchmark]
    public void Write()
    {
        _object.Tires[0].Pressure = 5;
        _object.Tires[1].Pressure = 6;
        _object.Tires[2].Pressure = 7;
        _object.Tires[3].Pressure = 8;
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

    [Benchmark]
    public void AddLotsOfMethodSubjects()
    {
        _methodSubject.Children = Enumerable.Range(0, 1000)
            .Select(_ => new MethodSubject())
            .ToArray();
    }

    [Benchmark]
    public object? InvokeMethodViaRegistry()
    {
        return _registeredMethod.Invoke(_methodArguments);
    }
}
