using Namotion.Interceptor.Hosting.Tests.Models;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// A production seam, armed to report when the code under test reaches it and to hold there until the
/// test lets it past.
/// </summary>
/// <remarks>
/// Disposal disarms the seam and releases whatever is parked on it, so an assertion that fails while a
/// transition is held leaves no queue wedged on a hook nothing is going to release. That is why the arming
/// helpers on <see cref="TestHookExtensions"/> are meant to be held in a <c>using</c>.
/// </remarks>
internal sealed class TestHook : IDisposable
{
    /// <summary>
    /// How long anything waits on this hook. Long enough that only a broken build reaches it, and
    /// bounded so a broken build fails its test rather than hanging the run.
    /// </summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(30);

    private readonly Action _disarm;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reported;

    private TestHook(Action disarm) => _disarm = disarm;

    /// <summary>
    /// Runs inside the seam before it reports being reached, so whatever it does is in place by the time
    /// a test thread waiting for the seam returns. Runs for the first entry only: releasing the hook is
    /// permanent, so every later pass through the seam would otherwise repeat the side effect.
    /// </summary>
    public Action? OnReached { get; set; }

    /// <summary>Completes once the code under test has reached the seam.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Whether the seam has been reached, read without waiting for it.</summary>
    public bool WasReached => _reached.Task.IsCompleted;

    /// <summary>Lets what is held at the seam past it, and everything that reaches it afterwards.</summary>
    public void Release() => _released.TrySetResult();

    public Task WaitUntilReachedAsync() => _reached.Task.WaitAsync(HookTimeout);

    /// <summary>
    /// Waits for the seam from a thread that cannot await, such as another seam's body. Throws on the
    /// timeout rather than returning, symmetrically with the awaitable form: a seam that is never reached
    /// otherwise lets the test carry on and assert against a state nothing produced.
    /// </summary>
    public void WaitUntilReached()
    {
        if (!_reached.Task.Wait(HookTimeout))
        {
            throw new TimeoutException($"The seam was not reached within {HookTimeout.TotalSeconds:0} seconds.");
        }
    }

    public void Dispose()
    {
        _disarm();
        Release();
    }

    /// <summary>Arms a seam whose body is awaited, so holding it parks rather than blocking a thread.</summary>
    public static TestHook Arm(Action<Func<Task>?> seam)
    {
        var hook = new TestHook(() => seam(null));
        seam(hook.EnterAsync);
        return hook;
    }

    /// <summary>Arms a seam whose body is synchronous, so holding it blocks the thread that reached it.</summary>
    public static TestHook ArmBlocking(Action<Action?> seam)
    {
        var hook = new TestHook(() => seam(null));
        seam(hook.EnterAndBlock);
        return hook;
    }

    private Task EnterAsync()
    {
        ReportReached();
        return _released.Task;
    }

    private void EnterAndBlock()
    {
        ReportReached();
        _released.Task.Wait(HookTimeout);
    }

    /// <summary>
    /// Reports the first entry and nothing after it. The completion is set inside the same guard, so a
    /// waiter never returns before <see cref="OnReached"/> has finished.
    /// </summary>
    private void ReportReached()
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            OnReached?.Invoke();
            _reached.TrySetResult();
        }
    }
}

/// <summary>
/// Arms one <see cref="TestHook"/> per seam the hosting code carries. Named per seam rather than taking
/// the property, so a call site says which interleaving it drives and cannot arm an awaitable seam with
/// a blocking body.
/// </summary>
internal static class TestHookExtensions
{
    /// <summary>Holds the drain after it began and before it clears liveness.</summary>
    public static TestHook HoldAtDrain(this HostedServiceHandler handler)
        => TestHook.Arm(hook => handler.DrainTestHook = hook);

    /// <summary>Holds the drain between its snapshot and the stops it enqueues.</summary>
    public static TestHook HoldAtDrainEnqueue(this HostedServiceHandler handler)
        => TestHook.Arm(hook => handler.DrainEnqueueTestHook = hook);

    /// <summary>Holds the drain between its first wait for in flight transitions and the release loop.</summary>
    public static TestHook HoldAtDrainRelease(this HostedServiceHandler handler)
        => TestHook.Arm(hook => handler.DrainReleaseTestHook = hook);

    /// <summary>Holds an attach between its ownership take and the gate re-read after it.</summary>
    public static TestHook HoldAtOwnershipTake(this HostedServiceHandler handler)
        => TestHook.ArmBlocking(hook => handler.OwnershipTakenTestHook = hook);

    /// <summary>Holds a liveness write inside the graph mutation lock it is taken under.</summary>
    public static TestHook HoldAtLivenessWrite(this HostedServiceHandler handler)
        => TestHook.ArmBlocking(hook => handler.LivenessWriteTestHook = hook);

    /// <summary>Holds every transition on the slot's queue at the top of its body.</summary>
    public static TestHook HoldAtTransition(this HostedServiceSlot slot)
        => TestHook.Arm(hook => slot.TransitionTestHook = hook);

    /// <summary>Holds a take inside the queue lock, between the take and the enqueue.</summary>
    public static TestHook HoldAtQueueLock(this HostedServiceSlot slot)
        => TestHook.ArmBlocking(hook => slot.QueueLockTestHook = hook);

    /// <summary>Holds the subject inside its own <c>StopAsync</c>.</summary>
    public static TestHook HoldAtStop(this CountingHostedSubject subject)
        => TestHook.Arm(hook => subject.StopHold = hook);

    /// <summary>Holds a factory attachment's instance inside its own <c>StopAsync</c>.</summary>
    public static TestHook HoldAtStop(this TrackedBackgroundService instance)
        => TestHook.Arm(hook => instance.StopHold = hook);
}
