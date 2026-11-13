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
