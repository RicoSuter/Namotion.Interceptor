using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class PropertyLifecycleRefreshTests
{
    [Fact]
    public void WhenTheSubjectHandlesItsOwnPropertyLifecycle_ThenItReceivesTheChildIndexRefresh()
    {
        // The attach and detach callbacks reach a subject which implements the handler itself, so the refresh
        // has to as well, or the interface holds only for two of its three members.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var container = new SelfHandlingContainer(context) { Items = [first, second] };
        container.Refreshes.Clear();

        // Act
        container.Items = [second, first];

        // Assert
        var children = Assert.Single(container.Refreshes);

        Assert.Equal([second, first], children.Select(c => c.Subject));
        Assert.Equal([0, 1], children.Select(c => c.Index));
        Assert.All(children, c => Assert.Equal(nameof(SelfHandlingContainer.Items), c.Property.Name));
    }

    [Fact]
    public void WhenNothingIsRetained_ThenNoRefreshIsDispatched()
    {
        // Every child of a write which retains nothing already carries its index from its own attach event.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var container = new SelfHandlingContainer(context)
        {
            Items = [new Person { FirstName = "A" }]
        };

        container.Refreshes.Clear();

        // Act
        container.Items = [new Person { FirstName = "B" }];

        // Assert
        Assert.Empty(container.Refreshes);
    }
}
