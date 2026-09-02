using System.Collections;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

internal readonly struct NullableChildCollection(IEnumerable<Person> children) : IEnumerable<Person>
{
    public IEnumerator<Person> GetEnumerator() => children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[InterceptorSubject]
internal partial class NullableStructuralHolder
{
    public partial NullableChildCollection? Children { get; set; }
}

public class NullableStructuralCollectionTests
{
    [Fact]
    public void WhenNullableValueTypeContainsAChild_ThenGeneratedWritePublishesLifecycleOwnership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new NullableStructuralHolder(context);
        var child = new Person();

        // Act
        holder.Children = new NullableChildCollection([child]);

        // Assert
        Assert.Same(context, ((IInterceptorSubject)child).TryGetContext());
        var parent = Assert.Single(((IInterceptorSubject)child).GetParents());
        Assert.Same(holder, parent.Property.Subject);
        Assert.Equal(nameof(NullableStructuralHolder.Children), parent.Property.Name);
        Assert.Equal(0, parent.Index);
    }
}
