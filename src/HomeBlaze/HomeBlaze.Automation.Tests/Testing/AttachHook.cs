using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>
/// Runs an action on the writing thread once the subject attaches to the graph: after the registry registered it, so that
/// its paths resolve, and before its properties are attached, such as its derived properties.
/// </summary>
[RunsAfter(typeof(SubjectRegistry))]
internal sealed class AttachHook(IInterceptorSubject subject, Action onAttach) : ILifecycleHandler
{
    private int _hasRun;

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach && ReferenceEquals(change.Subject, subject) && Interlocked.Exchange(ref _hasRun, 1) == 0)
        {
            onAttach();
        }
    }
}
