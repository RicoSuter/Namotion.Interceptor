using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the origin semantics of a derived property that has a setter and a transforming getter.
/// The terminal publication uses its frozen input, followed by the getter's recalculated value.
/// </summary>
public class DerivedSetterSourceOriginTests
{
    [Fact]
    public void WhenSourceWritesDerivedSetterThatTransforms_ThenChangePublishesLocalOrigin()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;

        // A derived property whose getter trims (transforms) and whose setter stores the raw value.
        string? backing = null;
        var derived = registered.AddDerivedProperty<string?>(
            "Trimmed",
            getValue: _ => backing?.Trim(),
            setValue: (_, value) => backing = value);

        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: a source applies a value the derived getter will transform (trim).
        derived.Reference.SetValueFromSource(source, DateTimeOffset.UtcNow, null, "  hello  ");

        var changes = DrainUntilSentinel(context, subscription);
        var trimmedChanges = changes.Where(c => c.Property.Name == "Trimmed").ToList();

        // Assert: the terminal publishes its frozen input before derived recalculation publishes the
        // getter projection. Both are derived writes, so neither retains the stamped source origin.
        Assert.Collection(
            trimmedChanges,
            change => Assert.Equal("  hello  ", change.GetNewValue<string?>()),
            change => Assert.Equal("hello", change.GetNewValue<string?>()));
        Assert.All(trimmedChanges, c => Assert.Equal(ChangeOriginKind.Local, c.Origin.Kind));
        Assert.All(trimmedChanges, c => Assert.Null(c.Origin.Source));
    }

    // Writes a sentinel change on a fresh subject and drains the subscription up to it (excluded),
    // returning everything published before the sentinel; throws TimeoutException after 10 seconds.
    private static List<SubjectPropertyChange> DrainUntilSentinel(
        IInterceptorSubjectContext context, PropertyChangeQueueSubscription subscription)
    {
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (ReferenceEquals(change.Property.Subject, sentinel)
                && change.Property.Name == nameof(Person.LastName))
            {
                return changes;
            }
            changes.Add(change);
        }
        throw new TimeoutException("Sentinel notification was not received within 10 seconds.");
    }
}
