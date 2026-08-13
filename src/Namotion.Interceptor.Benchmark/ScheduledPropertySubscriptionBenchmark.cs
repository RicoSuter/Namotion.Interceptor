using System;
using System.Reactive.Concurrency;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures a scheduled per-property subscription (<c>property.Subscribe(observer, scheduler)</c>) where it
/// differs from the inline one: the cost of getting one change to the observer, and the cost of creating and
/// disposing the subscription. What a live subscription costs the writer is measured next to the idle and
/// inline rows it has to be compared against, in <see cref="PropertyChangeSubscriptionsBenchmark"/>.
///
/// Each state runs in its own process (BenchmarkDotNet default), because the per-property subscription live
/// count is a process-wide static that gates an idle write fast path.
///
/// The two delivery rows are self-contained: each enqueues a fixed number of changes and returns only once
/// the observer has received all of them, so <c>OperationsPerInvoke</c> turns every column into a per-change
/// figure. The wait puts the scheduler handoff latency inside the time column, which the allocation column
/// does not contain.
/// </summary>
[MemoryDiagnoser]
public class ScheduledPropertySubscriptionBenchmark
{
    private const string WriteValue = "benchmark-value";

    // One drain per change: small enough that the per-operation wait stays in the microsecond range.
    private const int BurstChanges = 64;

    // Four times ScheduledPropertySubscription.MaxBatch, so the drain has to hand off to a fresh work item
    // three times and the amortized scheduling cost is visible rather than rounded away.
    private const int BacklogChanges = 4096;

    private IInterceptorSubjectContext _context;
    private Car _car;
    private PropertyReference _property;

    private readonly NoOpPropertyChangeObserver _noOpObserver = new();

    private CountingObserver _observer;
    private DrainScheduler _drainScheduler;
    private ScheduledPropertySubscription _subscription;

    [GlobalSetup(Targets = new[] { nameof(DeliverOneChangePerBurst), nameof(DeliverSustainedBacklog) })]
    public void SetupDelivery()
    {
        _car = CreateCarInFreshContext();
        _observer = new CountingObserver();
        _drainScheduler = new DrainScheduler();
        _subscription = new PropertyReference(_car, nameof(Car.Name))
            .Subscribe(_observer, _drainScheduler);
    }

    [GlobalSetup(Targets = new[] { nameof(SubscribeInlineAndDispose), nameof(SubscribeScheduledAndDispose) })]
    public void SetupChurn()
    {
        _car = CreateCarInFreshContext();
        _property = new PropertyReference(_car, nameof(Car.Name));
    }

    /// <summary>
    /// Observer keeps up: each change is waited out before the next is written, so the queue is empty again
    /// and the drain has to be scheduled afresh. The dominant term is the scheduler handoff, not the work.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BurstChanges)]
    public void DeliverOneChangePerBurst()
    {
        var delivered = _observer.Count;
        for (var index = 0; index < BurstChanges; index++)
        {
            delivered++;
            _car.Name = WriteValue;
            WaitForDeliveries(delivered);
        }
    }

    /// <summary>
    /// Observer falls behind: the drain is held until every change is queued, so it runs the full batch
    /// budget per work item and the scheduling cost amortizes over MaxBatch changes. Holding is what makes
    /// the backlog deterministic; a no-op observer otherwise outruns the writer and never builds one.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BacklogChanges)]
    public void DeliverSustainedBacklog()
    {
        var delivered = _observer.Count + BacklogChanges;

        _drainScheduler.Hold();
        for (var index = 0; index < BacklogChanges; index++)
        {
            _car.Name = WriteValue;
        }
        _drainScheduler.Release();

        WaitForDeliveries(delivered);
    }

    // The observer instance is reused so both churn rows measure only the subscription machinery. The
    // delegate overloads add one DelegateObserver to each, identically.
    [Benchmark]
    public void SubscribeInlineAndDispose()
    {
        _property.SubscribeInline(_noOpObserver).Dispose();
    }

    [Benchmark]
    public void SubscribeScheduledAndDispose()
    {
        // Nothing is ever written here, so the scheduler is never called and only the subscription's own
        // allocations (dispatcher, forwarder, queue and its first segment) separate this from the inline row.
        _property.Subscribe(_noOpObserver, Scheduler.Default).Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Scheduler work items per change is the mechanism behind the allocation gap between the two delivery
        // regimes, and BenchmarkDotNet has no column for it. Read before the dispose, which drops the queue
        // and would hide how large its segment had grown. Heap randomization re-runs the global setup after
        // every iteration, so each line covers one iteration rather than the whole run.
        if (_drainScheduler is not null)
        {
            var changes = _observer.Count;
            var workItems = _drainScheduler.WorkItemCount;
            Console.WriteLine(
                $"// work items: {workItems}, changes delivered: {changes}, " +
                $"work items per change: {(changes == 0 ? 0 : workItems / (double)changes):0.#####}, " +
                $"managed heap held: {GC.GetTotalMemory(forceFullCollection: true) / 1024} KB");
        }

        _subscription?.Dispose();
    }

    private void WaitForDeliveries(int target)
    {
        // sleep1Threshold -1 keeps the wait off Thread.Sleep(1), whose millisecond granularity would swamp a
        // handoff measured in microseconds.
        var spinWait = new SpinWait();
        while (_observer.Count < target)
        {
            spinWait.SpinOnce(-1);
        }
    }

    private Car CreateCarInFreshContext()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        return new Car(_context);
    }

    private sealed class CountingObserver : IPropertyChangeObserver
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        // Deliveries are serialized within one subscription and chained by the dispatcher's interlocked
        // work-in-progress accounting, so the read needs no interlocked form; the volatile store is what
        // publishes the count to the waiting benchmark thread.
        public void OnChange(in SubjectPropertyChange change) => Volatile.Write(ref _count, _count + 1);
    }

}

internal sealed class NoOpPropertyChangeObserver : IPropertyChangeObserver
{
    public void OnChange(in SubjectPropertyChange change)
    {
    }
}
