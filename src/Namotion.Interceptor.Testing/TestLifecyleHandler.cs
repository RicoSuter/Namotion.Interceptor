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

        public void OnLifecycleEvent(SubjectLifecycleChange change)
        {
            if (change.IsAttached)
            {
                _events.Add($"OnAttached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
            }

            if (change.IsDetached)
            {
                _events.Add($"OnDetached: {change.Subject} at {change.Property?.Name} with index {change.Index}, count: {change.ReferenceCount}");
            }
        }
    }
}
