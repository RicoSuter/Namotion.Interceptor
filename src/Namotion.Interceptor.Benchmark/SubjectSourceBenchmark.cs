using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SubjectSourceBenchmark
{
    private TestSubjectSource _source;
    private SubjectSourceBackgroundService _service;
    private IInterceptorSubjectContext _context;
    private CancellationTokenSource _cts;
    private Car _car;
    private string[] _propertyNames;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _source = new TestSubjectSource();
        _service = new SubjectSourceBackgroundService(
            _source,
            _context,
            NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(1),
            retryTime: TimeSpan.FromSeconds(1));

        _car = new Car(_context);
        _propertyNames = Enumerable
            .Range(1, 5000)
            .Select(i => $"Name{i}")
            .ToArray();
        
        foreach (var name in _propertyNames)
        {
            _car.TryGetRegisteredSubject()!
                .AddProperty(name, typeof(string), static _ => "foo", static (_, _) => { });
        }
        
        _cts = new CancellationTokenSource();
        await _service.StartAsync(_cts.Token);
    }

    [Benchmark]
    public void WriteToRegistrySubjects()
    {
        var c = 0;
        for (var i = 0; i < 1000000; i++)
        {
            _service.EnqueueSubjectUpdate(() => { c++;});
        }
        while (c < 1000000)
        {
            Thread.Sleep(1);
        }
    }

    [Benchmark]
    public void WriteToSource()
    {
        var observable = _context.GetService<PropertyChangedChannel>();
        for (var i = 0; i < _propertyNames.Length; i++)
        {
            var context = new PropertyWriteContext<int>(
                new PropertyReference(_car, _propertyNames[i]), 
                0, 
                i);

            observable.WriteProperty(ref context, (ref PropertyWriteContext<int> _) => {});
        }
        
        while (_source.Count < _propertyNames.Length)
        {
            Thread.Sleep(1);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cts.CancelAsync();
        await _service.StopAsync(CancellationToken.None);
        _cts.Dispose();
        _service.Dispose();
    }

    private class TestSubjectSource : ISubjectSource
    {
        public int Count { get; set; }
        
        public bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

        public Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken)
        {
            return Task.FromResult<IDisposable?>(null);
        }

        public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Action?>(null);
        }

        public ValueTask WriteToSourceAsync(IReadOnlyCollection<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            Count += changes.Count;
            return ValueTask.CompletedTask;
        }
    }
}
