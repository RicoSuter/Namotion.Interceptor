using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows()
    {
        // Arrange
        // The one-shot flag is load-bearing twice over. The handler also fires during stranger's
        // OWN construction, while the local is still null, which would record a
        // NullReferenceException and gate out every later invocation. And pre-fix the write
        // succeeds, so without the flag each attempt publishes another attach and recurses.
        Exception? callbackException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null)
            {
                return;
            }

            attempted = true;
            callbackException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.NotNull(root);
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows()
    {
        // Arrange: three levels, so the callback for the deepest subject runs inside the
        // descent's own callback scope. This is the case a single-level test cannot see.
        Exception? deepException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null || change.Subject is not Person { FirstName: "leaf" })
            {
                return;
            }

            attempted = true;
            deepException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        var top = new Person(context) { FirstName = "top" };
        var mid = new Person { FirstName = "mid" };
        var leaf = new Person { FirstName = "leaf" };
        mid.Father = leaf;

        // Act
        top.Father = mid;

        // Assert
        Assert.IsType<LifecycleContractViolationException>(deepException);
    }
}
