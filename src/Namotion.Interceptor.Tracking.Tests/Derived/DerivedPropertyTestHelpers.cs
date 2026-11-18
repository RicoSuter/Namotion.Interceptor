using System.Diagnostics;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Derived;

internal static class DerivedPropertyTestHelpers
{
    public static List<SubjectPropertyChange> SubscribeToChanges(IInterceptorSubjectContext context)
    {
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        return changes;
    }
}

internal sealed class CallbackLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        callback(change);
    }
}

[RunsAfter(typeof(SubjectRegistry))]
[RunsAfter(typeof(ContextInheritanceHandler))]
[RunsAfter(typeof(ParentTrackingHandler))]
internal sealed class AfterRegistryLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        callback(change);
    }
}

[RunsBefore(typeof(ContextInheritanceHandler))]
internal sealed class BeforeContextInheritanceLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        callback(change);
    }
}

/// <summary>Captures the trace output until it is disposed.</summary>
internal sealed class TraceCapture : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly TextWriterTraceListener _listener;

    public TraceCapture()
    {
        _listener = new TextWriterTraceListener(_output);
        Trace.Listeners.Add(_listener);
    }

    public string Text
    {
        get
        {
            _listener.Flush();
            return _output.ToString();
        }
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_listener);
        _listener.Dispose();
        _output.Dispose();
    }
}

/// <summary>A trace listener that throws on every write.</summary>
internal sealed class ThrowingTraceListener : TraceListener
{
    public override void Write(string? message) => throw new InvalidOperationException("Trace listener failure.");

    public override void WriteLine(string? message) => throw new InvalidOperationException("Trace listener failure.");
}

/// <summary>Calls the callback with each trace line, on the tracing thread, until it is disposed.</summary>
internal sealed class CallbackTraceListener : TraceListener
{
    private readonly Action<string> _callback;

    public CallbackTraceListener(Action<string> callback)
    {
        _callback = callback;
        Trace.Listeners.Add(this);
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        _callback(message ?? string.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}

/// <summary>Parks the first evaluation or delivery that passes it after <see cref="Arm"/> until resumed.</summary>
internal sealed class EvaluationGate
{
    private int _isArmed;
    private volatile bool _hasTimedOut;

    public ManualResetEventSlim Paused { get; } = new();

    public ManualResetEventSlim Resume { get; } = new();

    public bool HasTimedOut => _hasTimedOut;

    public void Arm() => Volatile.Write(ref _isArmed, 1);

    public void PauseIfArmed()
    {
        if (Interlocked.Exchange(ref _isArmed, 0) == 1)
        {
            Paused.Set();
            _hasTimedOut = !Resume.Wait(TestTimeouts.Default);
        }
    }
}
