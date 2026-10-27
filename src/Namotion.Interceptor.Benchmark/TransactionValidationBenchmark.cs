using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures property writes and transaction commits with property validation enabled.
/// </summary>
[MemoryDiagnoser]
public class TransactionValidationBenchmark
{
    private const int PropertyCount = 50;

    private IInterceptorSubjectContext _context;
    private Car _car;
    private Tire[] _tires;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithTransactions()
            .WithDataAnnotationValidation()
            .WithService<IPropertyValidator>(() => new NonNegativeDecimalValidator());

        _car = new Car(_context);
        _tires = Enumerable.Range(0, PropertyCount).Select(_ => new Tire()).ToArray();
        _car.Tires = _tires;
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
