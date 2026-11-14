using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures property writes and transaction commits with property validation enabled. With <see cref="WithSource"/>,
/// every written property is bound to a source, so a commit also writes to the source and validates again before the
/// local apply.
/// </summary>
[MemoryDiagnoser]
public class TransactionValidationBenchmark
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
            .WithTransactions()
            .WithDataAnnotationValidation()
            .WithService<IPropertyValidator>(() => new NonNegativeDecimalValidator(), validator => validator is NonNegativeDecimalValidator);

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
    public void WriteWithoutTransaction()
    {
        var value = Interlocked.Increment(ref _counter);
        for (var i = 0; i < _tires.Length; i++)
        {
            _tires[i].Pressure_Minimum = value + i;
        }
    }

    [Benchmark]
    public async Task CommitTransaction()
    {
        var value = Interlocked.Increment(ref _counter);

        using var transaction = await _context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        for (var i = 0; i < _tires.Length; i++)
        {
            _tires[i].Pressure_Minimum = value + i;
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Rejects negative decimals. The benchmark only writes positive values, so it always passes but still reads
    /// the value on every validation.
    /// </summary>
    private sealed class NonNegativeDecimalValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            return context.Value is decimal value && value < 0
                ? [new ValidationResult($"{context.Property.Name} must not be negative.")]
                : [];
        }
    }
}
