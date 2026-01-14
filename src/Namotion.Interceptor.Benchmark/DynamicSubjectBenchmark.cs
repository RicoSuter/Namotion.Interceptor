using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[MemoryDiagnoser]
public class DynamicSubjectBenchmark
{
    private IInterceptorSubjectContext? _context;
    private IInterceptorSubjectContext? _iterationContext;
    private IMotor? _motor;

    public interface IMotor
    {
        int Speed { get; set; }
    }

    public interface ISensor
    {
        int Temperature { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        
        var motor = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        motor.Context.AddFallbackContext(_context);
        _motor = (IMotor)motor;
    }
    
    [IterationSetup]
    public void IterationSetup()
    {
        _iterationContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }
    
    //[Benchmark]
    public void CreateDynamicSubject()
    {        
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        subject.Context.AddFallbackContext(_iterationContext!);
    }
    
    //[Benchmark]
    public void ReadDynamicProperty()
    {
        _ = _motor!.Speed;
    }
    
    //[Benchmark]
    public void WriteDynamicProperty()
    {
        _motor!.Speed = 100;
    }
}