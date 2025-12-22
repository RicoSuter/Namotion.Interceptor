using Xunit;

namespace Namotion.Interceptor.Tests
{
    public class ContextRecursionTests
    {
        [Fact]
        public void WhenContextsHaveCircularDependency_ThenOnContextChangedDoesNotStackOverflow()
        {
            var ctx1 = new InterceptorSubjectContext();
            var ctx2 = new InterceptorSubjectContext();

            // Create circular dependency
            ctx1.AddFallbackContext(ctx2);
            ctx2.AddFallbackContext(ctx1);

            // Trigger OnContextChanged
            ctx1.AddService("test");
            
            // Verify GetServices also works
            var services = ctx1.GetServices<string>();
            Assert.Contains("test", services);
        }
    }
}