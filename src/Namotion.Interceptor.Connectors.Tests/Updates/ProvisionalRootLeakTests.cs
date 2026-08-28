using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// An applier attaches a newly created item as a provisional root so the population below runs
/// registered and intercepted. The assignment that follows is what consumes the anchor, so a throw
/// in between must not leave the item attached: a provisional root is never released by
/// reachability, and nothing else refers to it.
/// </summary>
public class ProvisionalRootLeakTests
{
    [Fact]
    public void WhenApplyingANewChildThrows_ThenNoProvisionalRootIsLeftAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "John", Father = new Person { FirstName = "Bob" } };
        var target = new Person(context);

        var registry = context.GetService<ISubjectRegistry>();
        var knownBefore = registry.KnownSubjects.Count;

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Act: fail while the newly created father is attached but not yet assigned.
        var exception = Record.Exception(() => target.ApplySubjectUpdate(
            update,
            DefaultSubjectFactory.Instance,
            ChangeOrigin.Local,
            (property, _) =>
            {
                if (property.Subject != target && property.Name == nameof(Person.FirstName))
                {
                    throw new InvalidOperationException("apply failed");
                }
            }));

        // Assert
        Assert.NotNull(exception);
        Assert.Null(target.Father);
        Assert.Equal(knownBefore, registry.KnownSubjects.Count);
    }
}
