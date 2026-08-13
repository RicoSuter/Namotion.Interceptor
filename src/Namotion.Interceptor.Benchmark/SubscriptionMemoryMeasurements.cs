using System;
using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using System.Threading;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Two figures BenchmarkDotNet has no column for, printed by running the benchmark project with
/// <c>--subscription-memory</c>. These are single-shot measurements, not benchmark results.
///
/// The first is the memory a host holds for as long as its subscriptions live, which for a model with one
/// subscription per property is what the process pays at startup and keeps. The second is the allocation the
/// writing thread itself pays per write, which the MemoryDiagnoser cannot show because its accounting is
/// process-wide and therefore folds in whatever the drain thread allocated meanwhile.
///
/// The allocated figures come from the precise per-thread counter, and everything a subscribe call allocates
/// stays reachable from the subscription, so they are also the retained figures. The heap deltas come from
/// <see cref="GC.GetTotalMemory(bool)"/> and are corroboration only: the calibration line prints the same two
/// numbers for an object of known composition so the overstatement is visible rather than assumed.
/// </summary>
internal static class SubscriptionMemoryMeasurements
{
    private const string WriteValue = "benchmark-value";
    private const int SubscriptionCount = 1000;
    private const int WriteCount = 500_000;

    // Every one of these stays queued, at 144 bytes of change plus the slot around it, so the count is kept
    // well under the writer-allocation run's.
    private const int BacklogWriteCount = 100_000;
    private const int WarmupWriteCount = 10_000;

    public static void Report()
    {
        Console.WriteLine($"SubjectPropertyChange size: {Unsafe.SizeOf<SubjectPropertyChange>()} bytes");
        Console.WriteLine();
        MeasureCalibration();

        Console.WriteLine($"Retained memory of {SubscriptionCount} live subscriptions on {SubscriptionCount} distinct subjects");
        MeasureRetained("inline", static (property, observer) => property.SubscribeInline(observer));
        MeasureRetained("scheduled", static (property, observer) => property.Subscribe(observer, Scheduler.Default));

        Console.WriteLine();
        Console.WriteLine($"Writing-thread allocation over {WriteCount:N0} writes of one property");
        MeasureWriterAllocation("no subscription", null);
        MeasureWriterAllocation("inline", static (property, observer) => property.SubscribeInline(observer));
        MeasureWriterAllocation("scheduled", static (property, observer) => property.Subscribe(observer, Scheduler.Default));
        MeasureBacklogGrowth();
    }

    // A bare queue of the same element type is the bulk of what a scheduled subscription holds, and nothing
    // here becomes garbage, so the three counters below have to agree. They do not: the heap delta comes out
    // at twice the allocated bytes, which is how much to discount every heap delta printed afterwards.
    private static void MeasureCalibration()
    {
        var queues = new ConcurrentQueue<SubjectPropertyChange>[SubscriptionCount];

        var baseline = GetSettledTotalMemory();
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var processBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (var index = 0; index < queues.Length; index++)
        {
            queues[index] = new ConcurrentQueue<SubjectPropertyChange>();
        }

        var threadAllocated = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        var processAllocated = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
        var live = GetSettledTotalMemory();

        GC.KeepAlive(queues);

        Console.WriteLine($"Calibration: an empty ConcurrentQueue<SubjectPropertyChange>, which allocates nothing that dies");
        Console.WriteLine($"    allocated, this thread        {threadAllocated / (double)SubscriptionCount,10:0.#} bytes/queue");
        Console.WriteLine($"    allocated, whole process      {processAllocated / (double)SubscriptionCount,10:0.#} bytes/queue");
        Console.WriteLine($"    heap delta                    {(live - baseline) / (double)SubscriptionCount,10:0.#} bytes/queue");
        Console.WriteLine();
    }

    private static void MeasureRetained(string label, Func<PropertyReference, IPropertyChangeObserver, IDisposable> subscribe)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        var subjects = new Car[SubscriptionCount];
        for (var index = 0; index < subjects.Length; index++)
        {
            subjects[index] = new Car(context);
        }

        var observer = new NoOpPropertyChangeObserver();
        var subscriptions = new IDisposable[SubscriptionCount];

        // Baseline taken with the subjects already built, so the delta is the subscriptions and the property
        // data entries they add, not the model.
        var baseline = GetSettledTotalMemory();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < subjects.Length; index++)
        {
            subscriptions[index] = subscribe(new PropertyReference(subjects[index], nameof(Car.Name)), observer);
        }
        var subscribeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var live = GetSettledTotalMemory();

        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
        var disposeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var afterDispose = GetSettledTotalMemory();

        Array.Clear(subscriptions);
        var afterHandlesDropped = GetSettledTotalMemory();

        GC.KeepAlive(subjects);
        GC.KeepAlive(subscriptions);
        GC.KeepAlive(observer);

        Console.WriteLine($"  {label}");
        Console.WriteLine($"    allocated by subscribe        {subscribeAllocated / (double)SubscriptionCount,10:0.#} bytes/subscription");
        Console.WriteLine($"    allocated by dispose          {disposeAllocated / (double)SubscriptionCount,10:0.#} bytes/subscription");
        Console.WriteLine($"    heap delta, live              {(live - baseline) / (double)SubscriptionCount,10:0.#} bytes/subscription");
        Console.WriteLine($"    heap delta, disposed          {(afterDispose - baseline) / (double)SubscriptionCount,10:0.#} bytes/subscription (handle still referenced)");
        Console.WriteLine($"    heap delta, handles dropped   {(afterHandlesDropped - baseline) / (double)SubscriptionCount,10:0.#} bytes/subscription");
    }

    // What a change costs while the queue is genuinely growing, which is the one regime the delivery
    // benchmarks cannot show: they drain every burst, so their queue segment ends up big enough to be reused
    // as a ring and stops allocating. Holding the drain for the whole run keeps every enqueued change alive
    // and forces one slot per change.
    private static void MeasureBacklogGrowth()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        var subject = new Car(context);
        var drainScheduler = new DrainScheduler();
        drainScheduler.Hold();

        using var subscription = new PropertyReference(subject, nameof(Car.Name))
            .Subscribe(new NoOpPropertyChangeObserver(), drainScheduler);

        for (var index = 0; index < WarmupWriteCount; index++)
        {
            subject.Name = WriteValue;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < BacklogWriteCount; index++)
        {
            subject.Name = WriteValue;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var pending = subscription.PendingCount;
        var heap = GC.GetTotalMemory(forceFullCollection: false);

        Console.WriteLine(
            $"  {"undrained backlog",-17} {allocated / (double)BacklogWriteCount,8:0.###} bytes/write on the writing thread " +
            $"({pending:N0} pending, {heap / (1024 * 1024)} MB managed heap)");
    }

    private static void MeasureWriterAllocation(string label, Func<PropertyReference, IPropertyChangeObserver, IDisposable>? subscribe)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        var subject = new Car(context);
        var property = new PropertyReference(subject, nameof(Car.Name));
        var subscription = subscribe?.Invoke(property, new NoOpPropertyChangeObserver());

        // The queue grows its segments geometrically, so the steady-state per-write cost is only reached
        // once the segment size has stopped doubling.
        for (var index = 0; index < WarmupWriteCount; index++)
        {
            subject.Name = WriteValue;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < WriteCount; index++)
        {
            subject.Name = WriteValue;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var pending = DrainAndReportPending(subscription);
        subscription?.Dispose();

        Console.WriteLine(
            $"  {label,-15} {allocated / (double)WriteCount:0.###} bytes/write on the writing thread{pending}");
    }

    private static string DrainAndReportPending(IDisposable? subscription)
    {
        if (subscription is not ScheduledPropertySubscription scheduled)
        {
            return string.Empty;
        }

        var spinWait = new SpinWait();
        while (scheduled.PendingCount > 0)
        {
            spinWait.SpinOnce(-1);
        }

        return scheduled.IsFaulted ? " (subscription FAULTED, figure is meaningless)" : " (queue drained)";
    }

    private static long GetSettledTotalMemory()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
