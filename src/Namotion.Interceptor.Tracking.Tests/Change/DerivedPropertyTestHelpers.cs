using System.Diagnostics;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Change;

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
