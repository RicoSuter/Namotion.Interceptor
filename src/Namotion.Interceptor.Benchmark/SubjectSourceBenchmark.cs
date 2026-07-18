using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SubjectSourceBenchmark
{
    private TestSubjectSource _source;
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
        _source = new TestSubjectSource(
            _car,
            _context,
            NullLogger.Instance,
            _propertyNames.Length,
            bufferTime: TimeSpan.FromMilliseconds(1),
            retryTime: TimeSpan.FromSeconds(1));

        var registeredSubject = _car.TryGetRegisteredSubject()!;
        foreach (var name in _propertyNames)
        {
            var property = registeredSubject.AddProperty(name, typeof(string), static _ => "foo", static (_, _) => { });
            property.Reference.SetSource(_source);
        }

        _cts = new CancellationTokenSource();
        await _source.StartAsync(_cts.Token);
        _source.WaitForInitialization();

        _propertyWriter = _source.PropertyWriter!;

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

        var queue = _context.GetService<PropertyChangeInterceptor>();
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
        await _source.StopAsync(CancellationToken.None);
        _cts.Dispose();
        _source.Dispose();
    }

    private class TestSubjectSource : SubjectSourceBase
    {
        private readonly IInterceptorSubject _subject;
        private readonly int _targetCount;
        private readonly AutoResetEvent _signal = new(false);
        private readonly ManualResetEventSlim _initialized = new(false);
        private SubjectPropertyWriter? _propertyWriter;
        private int _count;

        public TestSubjectSource(
            IInterceptorSubject subject,
            IInterceptorSubjectContext context,
            ILogger logger,
            int targetCount,
            TimeSpan? bufferTime = null,
            TimeSpan? retryTime = null)
            : base(context, logger, bufferTime, retryTime, writeRetryQueueSize: 0)
        {
            _subject = subject;
            _targetCount = targetCount;
        }

        public override IInterceptorSubject RootSubject => _subject;

        public override int WriteBatchSize => int.MaxValue;

        internal SubjectPropertyWriter? PropertyWriter => _propertyWriter;

        public void Reset()
        {
            _count = 0;
        }

        public void Wait()
        {
            _signal.WaitOne();
        }

        protected override Task<IAsyncDisposable?> StartListeningAsync(
            SubjectPropertyWriter propertyWriter,
            CancellationToken cancellationToken)
        {
            _propertyWriter = propertyWriter;
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            _initialized.Set();
            return Task.FromResult<Action?>(null);
        }

        public void WaitForInitialization() => _initialized.Wait();

        public override ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            CancellationToken cancellationToken)
        {
            _count += changes.Length;

            if (_count >= _targetCount)
            {
                _signal.Set();
            }

            return new ValueTask<WriteResult>(WriteResult.Success);
        }
    }
}
