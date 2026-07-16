using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins that a derived recalculation triggered by a source-originated write publishes as local
/// origin, even for a subject whose IRaisePropertyChanged is hand-written (not generated). The
/// one-shot pending origin stamps only the triggering FirstName write; the nested derived
/// recalculation never consumes it, so the derived change is a locally computed value.
/// </summary>
public class DerivedPropertyLocalOriginTests
{
    [Fact]
    public void WhenDerivedRecalculationTriggeredBySourceWrite_ThenManualInpcDerivedChangePublishesLocalOrigin()
    {
        // Arrange: the subject inherits a hand-written (unwrapped) IRaisePropertyChanged base; the
        // derived FullName recalculates when FirstName changes.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new ManualInpcDerivedPerson(context);
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: write FirstName from a source, like an inbound apply.
        new PropertyReference(person, nameof(ManualInpcDerivedPerson.FirstName))
            .SetValueFromSource(source, DateTimeOffset.UtcNow, null, "John");

        var changes = DrainUntilSentinel(context, subscription);
        var personChanges = changes.Where(c => ReferenceEquals(c.Property.Subject, person)).ToList();
        var firstNameChanges = personChanges
            .Where(c => c.Property.Name == nameof(ManualInpcDerivedPerson.FirstName)).ToList();
        var fullNameChanges = personChanges
            .Where(c => c.Property.Name == nameof(ManualInpcDerivedPerson.FullName)).ToList();

        // Assert: FirstName carries the inbound source.
        Assert.Single(firstNameChanges);
        Assert.Same(source, firstNameChanges[0].Origin.Source);

        // The derived FullName recalculation publishes local origin, so it is never echo-suppressed
        // for the source that triggered it.
        Assert.Single(fullNameChanges);
        Assert.Equal(ChangeOriginKind.Local, fullNameChanges[0].Origin.Kind);
    }

    // Writes a sentinel change on a fresh subject and drains the subscription up to it (excluded),
    // returning everything published before the sentinel.
    private static List<SubjectPropertyChange> DrainUntilSentinel(
        IInterceptorSubjectContext context, PropertyChangeQueueSubscription subscription)
    {
        var sentinel = new ManualInpcDerivedPerson(context);
        sentinel.FirstName = "Sentinel";
        return ChangeQueueTestHelpers.DrainUntilSubject(subscription, sentinel);
    }
}
