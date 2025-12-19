using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
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

    private readonly AutoResetEvent _signal = new(false);
    private Action<object?>[] _updates;
    private SubjectPropertyWriter _propertyWriter;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _propertyNames = Enumerable
            .Range(1, 5000)
            .Select(i => $"Name{i}")
            .ToArray();

        _car = new Car(_context);
        _source = new TestSubjectSource(_propertyNames.Length) { RootSubject = _car };
        _service = new SubjectSourceBackgroundService(
            _source,
            _context,
            NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(1),
            retryTime: TimeSpan.FromSeconds(1));

        foreach (var name in _propertyNames)
        {
            _car.TryGetRegisteredSubject()!
                .AddProperty(name, typeof(string), static _ => "foo", static (_, _) => { });
        }

        _cts = new CancellationTokenSource();
        await _service.StartAsync(_cts.Token);

        _propertyWriter = _source.PropertyWriter;
        await _propertyWriter.CompleteInitializationAsync(_cts.Token);

        _updates = Enumerable
            .Range(1, 1000000)
            .Select(c => c < 1000000
                ? new Action<object?>(static _ => { })
                : _ =>
                {
                    _signal.Set();
                })
            .ToArray();
    }

    [Benchmark]
    public void WriteToRegistrySubjects()
    {
        for (var i = 0; i < _updates.Length; i++)
        {
            _propertyWriter.Write(null, _updates[i]);
        }

        _signal.WaitOne();
    }

    [Benchmark]
    public void WriteToSource()
    {
        _source.Reset();

        var queue = _context.GetService<PropertyChangeQueue>();
        for (var i = 0; i < _propertyNames.Length; i++)
        {
            var context = new PropertyWriteContext<int>(
                new PropertyReference(_car, _propertyNames[i]),
                0,
                i);

            queue.WriteProperty(ref context, (ref PropertyWriteContext<int> _) => {});
        }

        _source.Wait();
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
        private int _count;
        private readonly int _targetCount;
        private readonly AutoResetEvent _signal = new(false);

        public SubjectPropertyWriter PropertyWriter { get; private set; }

        public IInterceptorSubject RootSubject { get; set; }

        public TestSubjectSource(int targetCount)
        {
            _targetCount = targetCount;
        }

        public void Reset()
        {
            _count = 0;
        }

        public void Wait()
        {
            _signal.WaitOne();
        }

        public bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

        public Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
        {
            PropertyWriter = propertyWriter;
            return Task.FromResult<IDisposable?>(null);
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Action?>(null);
        }

        public int WriteBatchSize => int.MaxValue;

        public ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            _count += changes.Length;

            if (_count >= _targetCount)
            {
                _signal.Set();
            }

            return ValueTask.CompletedTask;
        }
    }
}
