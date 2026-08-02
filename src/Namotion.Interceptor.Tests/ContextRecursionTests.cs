using Xunit;

namespace Namotion.Interceptor.Tests;

public class ContextRecursionTests
{
    [Fact]
    public void WhenContextsHaveCircularDependency_ThenOnContextChangedDoesNotStackOverflow()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create();
        var context2 = InterceptorSubjectContext.Create();

        // Create circular dependency
        context1.AddFallbackContext(context2);
        context2.AddFallbackContext(context1);

        // Act - This would cause stack overflow before the fix
        context1.AddService("test");

        // Assert - Verify GetServices also works with circular dependency
        var services = context1.GetServices<string>();
        Assert.Contains("test", services);
    }
}
