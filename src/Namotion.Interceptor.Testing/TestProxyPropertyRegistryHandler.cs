using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing
{
    public class TestProxyPropertyRegistryHandler : ILifecycleHandler
    {
        private readonly List<LifecycleContext> _attaches;
        private readonly List<LifecycleContext> _detaches;

        public TestProxyPropertyRegistryHandler(
            List<LifecycleContext> attaches,
            List<LifecycleContext> detaches)
        {
            _attaches = attaches;
            _detaches = detaches;
        }

        public void Attach(LifecycleContext context)
        {
            _attaches.Add(context);
        }

        public void Detach(LifecycleContext context)
        {
            _detaches.Add(context);
        }
    }
}
