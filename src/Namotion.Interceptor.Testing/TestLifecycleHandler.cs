using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing
{
    public class TestLifecycleHandler : ILifecycleHandler, IReferenceLifecycleHandler
    {
        private readonly List<string> _events;

        public TestLifecycleHandler(List<string> events)
        {
            _events = events;
        }

        public void OnSubjectAttached(SubjectLifecycleChange change)
        {
            _events.Add($"Attached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}, first");
        }

        public void OnSubjectAttachedToProperty(SubjectLifecycleChange change)
        {
            _events.Add($"AttachedToProperty: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
        }

        public void OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
        {
            _events.Add($"DetachedFromProperty: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
        }

        public void OnSubjectDetached(SubjectLifecycleChange change)
        {
            _events.Add($"Detached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}, last");
        }
    }
}
