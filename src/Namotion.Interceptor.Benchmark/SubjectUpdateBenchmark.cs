using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SubjectUpdateBenchmark
{
    private const int DictionaryEntryCount = 100;

    private Car _car;
    private SubjectPropertyChange[] _changes;
    private ISubjectUpdateProcessor[] _processors;

    private Garage _garage;
    private SubjectPropertyChange[] _dictionaryReplacedChanges;
    private SubjectUpdate _dictionaryItemUpdate;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        _car = new Car(context);
        _changes =
        [
            SubjectPropertyChange.Create(new PropertyReference(_car.Tires[2], "Pressure"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 10d, 42d),
            SubjectPropertyChange.Create(new PropertyReference(_car, "Name"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "OldName", "NewName"),
            SubjectPropertyChange.Create(new PropertyReference(_car, nameof(Car.Name_MaxLength_Unit)), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "OldUnit", "NewUnit"),
            SubjectPropertyChange.Create(new PropertyReference(_car.Tires[1], "Pressure"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 10d, 42d),
        ];

        _processors = [JsonCamelCasePathProcessor.Instance];

        var previousCars = new Dictionary<string, Car>();
        for (var index = 0; index < DictionaryEntryCount - 1; index++)
        {
            previousCars["car" + index] = new Car();
        }

        var cars = new Dictionary<string, Car>(previousCars) { ["added"] = new Car() };

        _garage = new Garage(context) { ByName = previousCars };
        _garage.ByName = cars;

        _dictionaryReplacedChanges =
        [
            SubjectPropertyChange.Create(new PropertyReference(_garage, nameof(Garage.ByName)), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, previousCars, cars),
        ];

        // Built without processors: the receiver resolves properties by the names in the update.
        _dictionaryItemUpdate = SubjectUpdate.CreatePartialUpdateFromChanges(
            _garage,
            [SubjectPropertyChange.Create(new PropertyReference(cars["car50"], "Name"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "OldName", "NewName")],
            []);
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

    [Benchmark]
    public void CreateCompleteUpdateWithDictionary()
    {
        var subjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(_garage, _processors);
    }

    [Benchmark]
    public void CreatePartialUpdateWithReplacedDictionary()
    {
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(_garage, _dictionaryReplacedChanges, _processors);
    }

    [Benchmark]
    public void ApplyPartialUpdateBelowDictionary()
    {
        _garage.ApplySubjectUpdate(_dictionaryItemUpdate, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
    }
}