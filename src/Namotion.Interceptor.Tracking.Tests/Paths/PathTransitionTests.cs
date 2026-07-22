using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathTransitionTests
{
    public PathTransitionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedToResolvedPath_ThenOneListenerPerSegmentIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };

        // Act
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { });

        // Assert: two segments (Father, FirstName) => two per-property listeners.
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenIntermediateIsNull_ThenOnlyResolvablePrefixIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father is null: the chain build stops after subscribing to Father.

        // Act
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { });

        // Assert: only the resolvable prefix is installed (Father subscribed, FirstName not reached).
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }
}
