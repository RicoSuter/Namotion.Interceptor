using System;
using System.Collections.Generic;
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
        
        _cts = new CancellationTokenSource();
        await _service.StartAsync(_cts.Token);
    }

    [Benchmark]
    public void ProcessSourceChanges()
    {
        for (int i = 0; i < 100000; i++)
        {
            _service.EnqueueSubjectUpdate(() => { });
        }
    }

    [Benchmark]
    public void ProcessLocalChanges()
    {
        var observable = _context.GetService<PropertyChangedObservable>();
        for (var i = 0; i < 100000; i++)
        {
            var context = new PropertyWriteContext<int>(
                new PropertyReference(_car, "Name"), 
                0, 
                i);

            observable.WriteProperty(ref context, (ref PropertyWriteContext<int> _) => {});
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
        public bool IsPropertyIncluded(RegisteredSubjectProperty property) => false;

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
            return ValueTask.CompletedTask;
        }
    }
}
