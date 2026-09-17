using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class NonInterceptedPropertyLifecycleTests
{
    [Fact]
    public void WhenDerivedGetterReturnsAttachedSubject_ThenItIsNotAttachedAgain()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var holder = new DerivedSubjectHolder(context);
        var person = new Person { FirstName = "Child" };
        holder.Stored = person;

        // Act: the derived getter starts returning the same person
        holder.SelectStored = true;

        // Assert
        Assert.Same(person, holder.Selected);
        Assert.Equal(1, person.GetReferenceCount());
    }

    [Fact]
    public void WhenOwnerOfDerivedGetterIsDetached_ThenReferencedSubjectIsFullyDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new DerivedSubjectHolder(context);
        var holder = new DerivedSubjectHolder();
        var person = new Person { FirstName = "Child" };

        root.Child = holder;
        holder.Stored = person;
        holder.SelectStored = true;

        // Act
        root.Child = null;

        // Assert
        Assert.Equal(0, person.GetReferenceCount());
    }

    [Fact]
    public void WhenPartialDerivedPropertyReferencesSubject_ThenItStillCounts()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var holder = new DerivedSubjectHolder(context);
        var person = new Person { FirstName = "Child" };

        // Act
        holder.Stored = person;
        holder.StoredDerived = person;

        // Assert: a partial property stores its value, so [Derived] does not stop it counting
        Assert.Equal(2, person.GetReferenceCount());
    }
}
