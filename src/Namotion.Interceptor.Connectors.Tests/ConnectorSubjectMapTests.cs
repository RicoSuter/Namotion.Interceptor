using Moq;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class ConnectorSubjectMapTests
{
    [Fact]
    public void WhenAddingMapping_ThenCanLookupByExternalId()
    {
        // Arrange
        var (_, map) = CreateMap();
        var subject = CreateSubject();

        // Act
        map.Add("node-1", subject);

        // Assert
        Assert.True(map.TryGetSubject("node-1", out var found));
        Assert.Same(subject, found);
        Assert.Contains("node-1", map.ExternalIds);
    }

    [Fact]
    public void WhenAddingSameSubjectTwice_ThenRefCountIncrements()
    {
        // Arrange
        var (_, map) = CreateMap();
        var subject = CreateSubject();

        // Act
        map.Add("node-1", subject);
        map.Add("node-1", subject);

        // Assert: entry still exists and can be looked up
        Assert.True(map.TryGetSubject("node-1", out var found));
        Assert.Same(subject, found);

        // Removing once should not remove the entry (ref count was 2)
        var removed = map.Remove("node-1");
        Assert.False(removed);
        Assert.True(map.TryGetSubject("node-1", out _));
    }

    [Fact]
    public void WhenRemovingWithRefCountAboveOne_ThenEntryRemains()
    {
        // Arrange
        var (_, map) = CreateMap();
        var subject = CreateSubject();
        map.Add("node-1", subject);
        map.Add("node-1", subject); // ref count = 2

        // Act
        var removed = map.Remove("node-1");

        // Assert
        Assert.False(removed);
        Assert.True(map.TryGetSubject("node-1", out var found));
        Assert.Same(subject, found);
    }

    [Fact]
    public void WhenRemovingWithRefCountOne_ThenEntryIsRemoved()
    {
        // Arrange
        var (_, map) = CreateMap();
        var subject = CreateSubject();
        map.Add("node-1", subject);

        // Act
        var removed = map.Remove("node-1");

        // Assert
        Assert.True(removed);
        Assert.False(map.TryGetSubject("node-1", out _));
        Assert.DoesNotContain("node-1", map.ExternalIds);
    }

    [Fact]
    public void WhenSubjectDetaches_ThenEntryIsAutoCleaned()
    {
        // Arrange
        var (lifecycle, map) = CreateMap();
        var subject = CreateSubject();
        map.Add("node-1", subject);

        // Act
        lifecycle.RaiseSubjectDetaching(subject);

        // Assert
        Assert.False(map.TryGetSubject("node-1", out _));
        Assert.DoesNotContain("node-1", map.ExternalIds);
    }

    [Fact]
    public void WhenSubjectDetachesWithMultipleRefs_ThenRefCountDecrements()
    {
        // Arrange
        var (lifecycle, map) = CreateMap();
        var subject = CreateSubject();
        map.Add("node-1", subject);
        map.Add("node-1", subject); // ref count = 2

        // Act
        lifecycle.RaiseSubjectDetaching(subject);

        // Assert: entry still exists (ref count decremented from 2 to 1)
        Assert.True(map.TryGetSubject("node-1", out var found));
        Assert.Same(subject, found);

        // A second detach should remove it
        lifecycle.RaiseSubjectDetaching(subject);
        Assert.False(map.TryGetSubject("node-1", out _));
    }

    [Fact]
    public void WhenDisposed_ThenAllEntriesCleared()
    {
        // Arrange
        var (_, map) = CreateMap();
        var subject1 = CreateSubject();
        var subject2 = CreateSubject();
        map.Add("node-1", subject1);
        map.Add("node-2", subject2);

        // Act
        map.Dispose();

        // Assert
        Assert.False(map.TryGetSubject("node-1", out _));
        Assert.False(map.TryGetSubject("node-2", out _));
        Assert.Empty(map.ExternalIds);
    }

    private static (LifecycleInterceptor Lifecycle, ConnectorSubjectMap<string> Map) CreateMap()
    {
        var lifecycle = new LifecycleInterceptor();
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock
            .Setup(c => c.TryGetService<LifecycleInterceptor>())
            .Returns(lifecycle);

        var map = new ConnectorSubjectMap<string>(contextMock.Object);
        return (lifecycle, map);
    }

    private static IInterceptorSubject CreateSubject()
    {
        var mock = new Mock<IInterceptorSubject>();
        mock.Setup(s => s.Data)
            .Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        return mock.Object;
    }
}
