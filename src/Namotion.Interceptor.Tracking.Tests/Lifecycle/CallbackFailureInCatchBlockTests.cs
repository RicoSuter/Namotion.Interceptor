using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A callback failure reaches the nearest catch block of the caller also when the caller runs inside
/// a catch block itself, for example while it cleans up after another failure. On .NET 10.0.0 and
/// 10.0.1 an exception that leaves a finally block run on the normal path in that situation skips the
/// catch blocks in between, so these fail there when the gate drains its callbacks from a finally block.
/// </summary>
public class CallbackFailureInCatchBlockTests
{
    [Fact]
    public void WhenDetachCallbackFailsWhileCallerHandlesAnotherException_ThenTheCallerCatchesTheFailure()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var person = new Person(context);
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                throw new InvalidOperationException("detach failed");
            }
        };

        // Act
        var failure = RecordWhileHandlingAnotherException(() => person.DetachFromContext(context));

        // Assert
        Assert.Equal("detach failed", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Null(person.TryGetContext());
    }

    [Fact]
    public void WhenAttachCallbackFailsWhileCallerHandlesAnotherException_ThenTheCallerCatchesTheFailure()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var person = new Person();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (change.IsContextAttach)
            {
                throw new InvalidOperationException("attach failed");
            }
        };

        // Act
        var failure = RecordWhileHandlingAnotherException(() => person.AttachToContext(context));

        // Assert
        Assert.Equal("attach failed", Assert.IsType<InvalidOperationException>(failure).Message);
    }

    [Fact]
    public void WhenStructuralWriteCallbackFailsWhileCallerHandlesAnotherException_ThenTheCallerCatchesTheFailure()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var father = new Person();
        var child = new Person(context) { Father = father };
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, father))
            {
                throw new InvalidOperationException("detach failed");
            }
        };

        // Act
        var failure = RecordWhileHandlingAnotherException(() => child.Father = null);

        // Assert
        Assert.Equal("detach failed", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Null(child.Father);
        Assert.Null(father.TryGetContext());
    }

    private static Exception? RecordWhileHandlingAnotherException(Action action)
    {
        try
        {
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException)
        {
            return Record.Exception(action);
        }
    }
}
