using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing
{
    public class TestLifecyleHandler : ILifecycleHandler
    {
        private readonly List<string> _events;

        public TestLifecyleHandler(List<string> events)
        {
            _events = events;
        }

        public void AttachSubject(SubjectLifecycleChange change)
        {
            _events.Add($"Attached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
        }

        public void DetachSubject(SubjectLifecycleChange change)
        {
            _events.Add($"Detached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
        }
    }
}
