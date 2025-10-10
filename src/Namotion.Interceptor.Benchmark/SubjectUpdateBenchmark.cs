using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Updates;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SubjectUpdateBenchmark
{
    private Car _car;
    private SubjectPropertyChange[] _changes;
    private ISubjectUpdateProcessor[] _processors;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        _car = new Car(context);
        _changes =
        [
            SubjectPropertyChange.Create(new PropertyReference(_car.Tires[2], "Pressure"), null, DateTimeOffset.Now, 10d, 42d),
            SubjectPropertyChange.Create(new PropertyReference(_car, "Name"), null, DateTimeOffset.Now, "OldName", "NewName"),
            SubjectPropertyChange.Create(new PropertyReference(_car.Tires[1], "Pressure"), null, DateTimeOffset.Now, 10d, 42d),
        ];

        _processors = [JsonCamelCasePathProcessor.Instance];
    }

    [Benchmark]
    public void CreateCompleteUpdate()
    {
        var subjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(_car, _processors);
    }
    
    [Benchmark]
    public void CreatePartialUpdate()
    {
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(_car, _changes, _processors);    
    }
}