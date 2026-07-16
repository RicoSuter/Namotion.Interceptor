using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the origin semantics of a derived property that has a setter and a transforming getter: a
/// source apply stores the raw value, but the published value is the getter's recomputation, never
/// literally the value the source sent. The change must therefore publish with a Local origin so it
/// is not echo-suppressed for the applying source.
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

        // Assert: the stored/published value is the getter's recomputation ("hello"), never literally
        // the value the source sent ("  hello  "), so a stamped source origin cannot survive on the
        // derived write: every published change carries Local (and is not echo-suppressed).
        Assert.NotEmpty(trimmedChanges);
        Assert.All(trimmedChanges, c => Assert.Equal(ChangeOriginKind.Local, c.Origin.Kind));
        Assert.All(trimmedChanges, c => Assert.Null(c.Origin.Source));
        Assert.All(trimmedChanges, c => Assert.Equal("hello", c.GetNewValue<string?>()));
    }

    // Writes a sentinel change on a fresh subject and drains the subscription up to it (excluded),
    // returning everything published before the sentinel.
    private static List<SubjectPropertyChange> DrainUntilSentinel(
        IInterceptorSubjectContext context, PropertyChangeQueueSubscription subscription)
    {
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        return ChangeQueueTestHelpers.DrainUntilSubject(subscription, sentinel);
    }
}
