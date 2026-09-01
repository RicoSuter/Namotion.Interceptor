using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The topology gate is held across the whole write chain, so code running inside it that dispatches
/// structural work to another thread and waits for that thread deadlocks: the holder cannot release
/// the gate until the work finishes and the work cannot start until the gate is released. The
/// dispatch goes through a thread, a pool or a queue and is invisible from here, so this cannot be
/// rejected up front the way a second gate can. The gate wait is bounded instead, which turns the
/// hang into a named exception on the dispatched thread and leaves the holder free to finish.
/// </summary>
public class SameContextGateDeadlockTests
{
    /// <summary>Comfortably longer than the gate bound, so a hang is still reported as one.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAGateHolderWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFailsInsteadOfHanging()
    {
        // Arrange: a write interceptor downstream of the lifecycle runs while the gate is held, and
        // from there dispatches a structural write on the same context to a worker it then joins.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var workerTarget = new Person { FirstName = "worker target" };
        ((IInterceptorSubject)workerTarget).AttachToContext(context);

        var interceptor = new DispatchAndJoinOnceInterceptor(
            () => workerTarget.Father = new Person { FirstName = "fromWorker" },
            JoinTimeout);
        context.AddService<IWriteInterceptor>(interceptor);

        var trigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Act
        var stopwatch = Stopwatch.StartNew();
        trigger.Mother = new Person { FirstName = "fromHolder" };
        stopwatch.Stop();

        // Assert: the worker gave up rather than waiting forever, and said what the caller did.
        Assert.True(interceptor.WorkerJoined, "the dispatched worker never returned, so the gate wait is unbounded");
        var workerException = Assert.IsType<LifecycleContractViolationException>(interceptor.WorkerException);
        Assert.Contains("topology gate", workerException.Message);
        Assert.Contains("dispatched this work to another thread", workerException.Message);
        Assert.Contains("Nothing was read and nothing was changed", workerException.Message);
        Assert.True(stopwatch.Elapsed < JoinTimeout, "the write outlived the bound the worker was supposed to hit");

        // Assert: only the dispatched write failed. The holder's own write went through, and the
        // subject the worker could not attach is untouched.
        Assert.Equal("fromHolder", ((Person)trigger.Mother!).FirstName);
        Assert.Null(workerTarget.Father);
    }

    /// <summary>Dispatches one structural write to a worker thread and waits for it, once.</summary>
    private sealed class DispatchAndJoinOnceInterceptor(Action dispatchedWrite, TimeSpan joinTimeout) : IWriteInterceptor
    {
        private bool _fired;

        public bool WorkerJoined { get; private set; }

        public Exception? WorkerException { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!_fired)
            {
                _fired = true;
                var worker = new Thread(() => WorkerException = Record.Exception(dispatchedWrite)) { IsBackground = true };
                worker.Start();
                WorkerJoined = worker.Join(joinTimeout);
            }

            next(ref context);
        }
    }
}
