using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures the cost of committing a transaction that writes a batch of property changes.
/// The <see cref="WithSource"/> parameter switches between a local-only commit (changes applied
/// to the in-process model) and a source-bound commit (changes also dispatched to an external
/// source via the source transaction writer).
/// </summary>
[MemoryDiagnoser]
public class SubjectTransactionBenchmark
{
    private const int PropertyCount = 50;

    private IInterceptorSubjectContext _context;
    private Car _car;
    private Tire[] _tires;
    private int _counter;

    [Params(false, true)]
    public bool WithSource;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithTransactions();

        if (WithSource)
        {
            context.WithSourceTransactions();
        }

        _context = context;
        _car = new Car(_context);

        _tires = Enumerable.Range(0, PropertyCount).Select(_ => new Tire()).ToArray();
        _car.Tires = _tires;

        if (WithSource)
        {
            var source = new BenchmarkSource(_car);
            foreach (var tire in _tires)
            {
                new PropertyReference(tire, nameof(Tire.Pressure_Minimum)).SetSource(source);
            }
        }
    }

    [Benchmark]
    public async Task CommitChanges()
    {
        var value = Interlocked.Increment(ref _counter);

        using var transaction = await _context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        for (var i = 0; i < _tires.Length; i++)
        {
            _tires[i].Pressure_Minimum = value + i;
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private sealed class BenchmarkSource : ISubjectSource
    {
        public BenchmarkSource(IInterceptorSubject rootSubject)
        {
            RootSubject = rootSubject;
        }

        public IInterceptorSubject RootSubject { get; }

        public int WriteBatchSize => 0;

        public ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            return new ValueTask<WriteResult>(WriteResult.Success);
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Action?>(null);
        }
    }
}
