using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing
{
    public class TestLifecyleHandler : ILifecycleHandler
    {
        private readonly List<SubjectLifecycleUpdate> _attaches;
        private readonly List<SubjectLifecycleUpdate> _detaches;

        public TestLifecyleHandler(
            List<SubjectLifecycleUpdate> attaches,
            List<SubjectLifecycleUpdate> detaches)
        {
            _attaches = attaches;
            _detaches = detaches;
        }

        public void Attach(SubjectLifecycleUpdate update)
        {
            _attaches.Add(update);
        }

        public void Detach(SubjectLifecycleUpdate update)
        {
            _detaches.Add(update);
        }
    }
}
