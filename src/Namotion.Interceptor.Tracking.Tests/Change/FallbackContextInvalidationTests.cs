using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class FallbackContextInvalidationTests
{
    [Fact]
    public void WhenFallbackIsAttachedToContextUsedByAnotherContext_ThenTheUsingContextChainIsRebuilt()
    {
        // Arrange: the subject context has an own service, so it compiles and caches its own
        // interceptor chain instead of delegating everything to the intermediate context.
        var trackingContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var intermediateContext = InterceptorSubjectContext.Create();

        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService(new MarkerService());
        subjectContext.AddFallbackContext(intermediateContext);

        var person = new Person(subjectContext);
        person.LastName = "compiles the write chain";

        // Act: attaching the tracking context below the intermediate context has to invalidate
        // the already compiled chain of the subject context above it.
        intermediateContext.AddFallbackContext(trackingContext);

        var changes = new List<SubjectPropertyChange>();
        using var subscription = subjectContext
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        person.LastName = "Doe";

        // Assert
        Assert.Single(changes);
        Assert.Equal(nameof(Person.LastName), changes[0].Property.Name);
    }

    private sealed class MarkerService;
}
