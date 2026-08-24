using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A dictionary entry is an occurrence like a collection slot is, so the same subject under two keys
/// is two edges. Keys differ from collection indices in that they are stable identities: a reorder
/// cannot invalidate them, and a rename is a different occurrence rather than the same one moved.
/// </summary>
public class DictionaryOccurrenceTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenOneSubjectIsStoredUnderTwoKeys_ThenItHasTwoOccurrences()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Assert
        Assert.Equal(2, car.GetReferenceCount());
        var parents = ((IInterceptorSubject)car).GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, parent => Equals(parent.Index, "x"));
        Assert.Contains(parents, parent => Equals(parent.Index, "y"));
    }

    [Fact]
    public void WhenOneOfTwoKeysIsDropped_ThenTheSubjectKeepsTheSurvivingOccurrence()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["y"] = car };

        // Assert
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        var parents = ((IInterceptorSubject)car).GetParents();
        Assert.Single(parents);
        Assert.Equal("y", parents[0].Index);
    }

    [Fact]
    public void WhenBothKeysAreDropped_ThenTheSubjectDetaches()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Act
        garage.CarsByName = new Dictionary<string, Car>();

        // Assert
        Assert.Equal(0, car.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)car).TryGetContext());
        Assert.Empty(((IInterceptorSubject)car).GetParents());
    }

    [Fact]
    public void WhenTwoSubjectsSwapKeys_ThenBothOccurrencesAreRewritten()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var first = new Car { Name = "A" };
        var second = new Car { Name = "B" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = first, ["y"] = second };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = second, ["y"] = first };

        // Assert: keys identify the occurrences, so each subject moved to the other key rather than
        // keeping a stale one.
        Assert.Equal("y", ((IInterceptorSubject)first).GetParents()[0].Index);
        Assert.Equal("x", ((IInterceptorSubject)second).GetParents()[0].Index);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    [Fact]
    public void WhenTheOnlyKeyOfASubjectIsRenamed_ThenItDetachesAndReattaches()
    {
        // Arrange
        var context = CreateContext();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car };

        var attached = new List<IInterceptorSubject>();
        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);
        lifecycleInterceptor.SubjectDetaching += change => detached.Add(change.Subject);

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["y"] = car };

        // Assert: a key is an identity, so renaming the only key is a removal and an addition, not a
        // move. The subject therefore leaves the graph and re-enters it, and so does everything
        // below it, which a Registry projection observes as an eviction followed by a fresh
        // registration of the whole subtree.
        Assert.Contains(car, detached);
        Assert.Contains(car, attached);
        Assert.Equal(1 + car.Tires.Length, detached.Count);
        Assert.Equal(1 + car.Tires.Length, attached.Count);
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        Assert.Equal("y", ((IInterceptorSubject)car).GetParents()[0].Index);
    }
}
