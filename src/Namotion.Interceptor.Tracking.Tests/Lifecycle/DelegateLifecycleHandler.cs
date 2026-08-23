using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>Runs a delegate on every lifecycle change, for tests that react from inside a callback.</summary>
internal sealed class DelegateLifecycleHandler(Action<SubjectLifecycleChange> onChange) : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        onChange(change);
    }
}
