using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing
{
    public class TestLifecyleHandler : ILifecycleHandler
    {
        private readonly List<SubjectLifecycleChange> _attaches;
        private readonly List<SubjectLifecycleChange> _detaches;

        public TestLifecyleHandler(
            List<SubjectLifecycleChange> attaches,
            List<SubjectLifecycleChange> detaches)
        {
            _attaches = attaches;
            _detaches = detaches;
        }

        public void AttachSubject(SubjectLifecycleChange change)
        {
            _attaches.Add(change);
        }

        public void DetachSubject(SubjectLifecycleChange change)
        {
            _detaches.Add(change);
        }
    }
}
