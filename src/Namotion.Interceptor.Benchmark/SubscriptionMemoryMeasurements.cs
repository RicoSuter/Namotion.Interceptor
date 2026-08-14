using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
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
        MeasureRetained("inline observable", static (property, _) => property.GetInlineChangeObservable().Subscribe(NoOpRxObserver.Instance));
        MeasureRetained("scheduled", static (property, observer) => property.Subscribe(observer, Scheduler.Default));
        MeasureInlineObservableInstance();

        Console.WriteLine();
        Console.WriteLine("Marginal cost of one more consumer on ONE context (context-level channels)");
        MeasureContextChannelMarginalCost("queue", static context => context.CreatePropertyChangeQueueSubscription());
        MeasureContextChannelMarginalCost("default observable", static context => context.GetPropertyChangeObservable().Subscribe(NoOpRxObserver.Instance));

        Console.WriteLine();
        MeasureDispatchThreads();

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

    // The context-level channels are per context rather than per property, so "held per subscription" means
    // what one MORE consumer costs. Measuring each subscribe on its own separates that from the copy-on-write
    // consumer array both channels grow, which is quadratic in the consumer count over a run of subscribes but
    // is churn rather than retention: only the last array is held. The first subscribe additionally pays the
    // channel's one-time setup, which is why the marginal figures start at the second.
    private static void MeasureContextChannelMarginalCost(string label, Func<IInterceptorSubjectContext, IDisposable> subscribe)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        // A subject has to exist for the interceptor to be resolvable the way a caller would reach it.
        var subject = new Car(context);

        var subscriptions = new IDisposable[SubscriptionCount];
        var marginal = new long[SubscriptionCount];

        for (var index = 0; index < subscriptions.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            subscriptions[index] = subscribe(context);
            marginal[index] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var total = 0L;
        foreach (var value in marginal)
        {
            total += value;
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
        var disposeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        GC.KeepAlive(subject);
        GC.KeepAlive(subscriptions);

        Console.WriteLine($"  {label}");
        Console.WriteLine($"    first subscriber              {marginal[0],10} bytes (includes the channel's one-time setup)");
        Console.WriteLine($"    2nd / 3rd / 4th subscriber    {marginal[1],10} / {marginal[2]} / {marginal[3]} bytes");
        Console.WriteLine($"    100th / 1000th subscriber     {marginal[99],10} / {marginal[SubscriptionCount - 1]} bytes (grows by 8 bytes per existing consumer: the array copy)");
        Console.WriteLine($"    all {SubscriptionCount} subscribers          {total / (double)SubscriptionCount,10:0.#} bytes/subscription including that churn");
        Console.WriteLine($"    allocated by dispose          {disposeAllocated / (double)SubscriptionCount,10:0.#} bytes/subscription");
    }

    // Splits the inline observable's subscribe figure: the observable object is the only part of it a caller
    // who drops the observable after subscribing does not keep, the subscription holding the adapter instead.
    private static void MeasureInlineObservableInstance()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        var property = new PropertyReference(new Car(context), nameof(Car.Name));
        var observables = new IObservable<SubjectPropertyChange>[SubscriptionCount];

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < observables.Length; index++)
        {
            observables[index] = property.GetInlineChangeObservable();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(observables);

        Console.WriteLine($"  the observable object alone, no subscribe");
        Console.WriteLine($"    allocated                     {allocated / (double)SubscriptionCount,10:0.#} bytes");
    }

    // Managed heap is not all a subscriber holds. Rx resolves an ISchedulerLongRunning from Scheduler.Default,
    // so the ObserveOn sink behind the default observable owns a thread per subscriber for the life of the
    // subscription, taken on the first change rather than at subscribe. The scheduled per-property channel
    // schedules an ordinary work item instead and shares the thread pool. Counting the distinct threads
    // deliveries arrive on shows the difference without needing a platform thread count.
    private static void MeasureDispatchThreads()
    {
        const int subscriberCount = 50;

        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        var subject = new Car(context);
        var observableObservers = new DispatchThreadObserver[subscriberCount];
        var observableSubscriptions = new IDisposable[subscriberCount];
        for (var index = 0; index < subscriberCount; index++)
        {
            observableObservers[index] = new DispatchThreadObserver();
            observableSubscriptions[index] = context
                .GetPropertyChangeObservable()
                .Subscribe(observableObservers[index]);
        }

        // One change reaches every subscriber, which is what makes each sink take its thread.
        subject.Name = WriteValue;
        WaitForFirstDelivery(observableObservers);

        var scheduledObservers = new DispatchThreadObserver[subscriberCount];
        var scheduledSubjects = new Car[subscriberCount];
        var scheduledSubscriptions = new IDisposable[subscriberCount];
        for (var index = 0; index < subscriberCount; index++)
        {
            scheduledObservers[index] = new DispatchThreadObserver();
            scheduledSubjects[index] = new Car(context);
            scheduledSubscriptions[index] = new PropertyReference(scheduledSubjects[index], nameof(Car.Name))
                .Subscribe(scheduledObservers[index], Scheduler.Default);
        }

        foreach (var scheduledSubject in scheduledSubjects)
        {
            scheduledSubject.Name = WriteValue;
        }
        WaitForFirstDelivery(scheduledObservers);

        Console.WriteLine($"Threads deliveries arrive on, {subscriberCount} subscribers");
        ReportDispatchThreads("default observable", observableObservers);
        ReportDispatchThreads("scheduled per-property", scheduledObservers);

        foreach (var subscription in observableSubscriptions)
        {
            subscription.Dispose();
        }

        foreach (var subscription in scheduledSubscriptions)
        {
            subscription.Dispose();
        }
    }

    private static void WaitForFirstDelivery(DispatchThreadObserver[] observers)
    {
        var spinWait = new SpinWait();
        foreach (var observer in observers)
        {
            while (observer.ThreadId == 0)
            {
                spinWait.SpinOnce(-1);
            }
        }
    }

    private static void ReportDispatchThreads(string label, DispatchThreadObserver[] observers)
    {
        var threadIds = new HashSet<int>();
        var poolDeliveries = 0;
        foreach (var observer in observers)
        {
            threadIds.Add(observer.ThreadId);
            if (observer.IsThreadPoolThread)
            {
                poolDeliveries++;
            }
        }

        Console.WriteLine(
            $"  {label,-22} {threadIds.Count,3} distinct threads, {poolDeliveries} of {observers.Length} on a thread-pool thread");
    }

    // Both interfaces so one observer can be pointed at either channel.
    private sealed class DispatchThreadObserver : IObserver<SubjectPropertyChange>, IPropertyChangeObserver
    {
        private int _threadId;

        public int ThreadId => Volatile.Read(ref _threadId);

        public bool IsThreadPoolThread { get; private set; }

        public void OnNext(SubjectPropertyChange value) => Record();

        public void OnChange(in SubjectPropertyChange change) => Record();

        private void Record()
        {
            if (ThreadId != 0)
            {
                return;
            }

            IsThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
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

        using var subscription = new PropertyReference(subject, nameof(Car.Name))
            .Subscribe(new NoOpPropertyChangeObserver(), new SuspendedScheduler());

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

    // Accepts drain work items and never runs them, so the backlog never shrinks. Holding the drain is the
    // only way to reach a genuinely growing queue: a no-op observer otherwise outruns any single writer.
    private sealed class SuspendedScheduler : IScheduler
    {
        public DateTimeOffset Now => Scheduler.Default.Now;

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
            => Disposable.Empty;

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
            => Disposable.Empty;

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
            => Disposable.Empty;
    }

    private sealed class NoOpPropertyChangeObserver : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change)
        {
        }
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
