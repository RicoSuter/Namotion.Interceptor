using Moq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceOwnershipManagerTests
{
    [Fact]
    public void ClaimSource_WhenNotOwned_ReturnsTrue()
    {
        // Arrange
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(sourceMock.Object);
        var property = CreatePropertyReference();

        // Act
        var result = manager.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Contains(property, manager.Properties);
    }

    [Fact]
    public void ClaimSource_WhenOwnedBySameSource_ReturnsTrue()
    {
        // Arrange
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(sourceMock.Object);
        var property = CreatePropertyReference();

        // First claim
        manager.ClaimSource(property);

        // Act - Second claim with same source
        var result = manager.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Single(manager.Properties); // Should not duplicate
    }

    [Fact]
    public void ClaimSource_WhenOwnedByDifferentSource_ReturnsFalse()
    {
        // Arrange
        var source1Mock = CreateSourceMock();
        var source2Mock = CreateSourceMock();
        var manager1 = new SourceOwnershipManager(source1Mock.Object);
        var manager2 = new SourceOwnershipManager(source2Mock.Object);
        var property = CreatePropertyReference();

        // First source claims property
        manager1.ClaimSource(property);

        // Act - Second source tries to claim same property
        var result = manager2.ClaimSource(property);

        // Assert
        Assert.False(result);
        Assert.DoesNotContain(property, manager2.Properties);
    }

    [Fact]
    public void ReleaseSource_CallsOnReleasingCallback()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(
            sourceMock.Object,
            onReleasing: p => releasedProperties.Add(p));

        var property = CreatePropertyReference();
        manager.ClaimSource(property);

        // Act
        manager.ReleaseSource(property);

        // Assert
        Assert.Single(releasedProperties);
        Assert.Equal(property, releasedProperties[0]);
        Assert.DoesNotContain(property, manager.Properties);
    }

    [Fact]
    public void ReleaseSource_WhenNotOwned_DoesNothing()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(
            sourceMock.Object,
            onReleasing: p => releasedProperties.Add(p));

        var property = CreatePropertyReference();

        // Act - Release without claiming first
        manager.ReleaseSource(property);

        // Assert
        Assert.Empty(releasedProperties);
    }

    [Fact]
    public void Dispose_ReleasesAllProperties()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(
            sourceMock.Object,
            onReleasing: p => releasedProperties.Add(p));

        var property1 = CreatePropertyReference("Prop1");
        var property2 = CreatePropertyReference("Prop2");
        manager.ClaimSource(property1);
        manager.ClaimSource(property2);

        // Act
        manager.Dispose();

        // Assert
        Assert.Equal(2, releasedProperties.Count);
        Assert.Empty(manager.Properties);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_OnlyReleasesOnce()
    {
        // Arrange
        var releaseCount = 0;
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(
            sourceMock.Object,
            onReleasing: _ => releaseCount++);

        var property = CreatePropertyReference();
        manager.ClaimSource(property);

        // Act
        manager.Dispose();
        manager.Dispose();
        manager.Dispose();

        // Assert
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void SubjectDetached_ReleasesPropertiesForSubject()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var detachedSubjects = new List<IInterceptorSubject>();
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(
            sourceMock.Object,
            onReleasing: p => releasedProperties.Add(p),
            onSubjectDetaching: s => detachedSubjects.Add(s));

        // Create context with lifecycle interceptor
        var lifecycleInterceptor = new LifecycleInterceptor();
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<LifecycleInterceptor>()).Returns(lifecycleInterceptor);

        manager.SubscribeToLifecycle(contextMock.Object);

        // Create subjects and properties (subjects need Data for PropertyReference to work)
        var subject1Mock = new Mock<IInterceptorSubject>();
        subject1Mock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        var subject2Mock = new Mock<IInterceptorSubject>();
        subject2Mock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        var property1 = new PropertyReference(subject1Mock.Object, "Prop1");
        var property2 = new PropertyReference(subject2Mock.Object, "Prop2");
        var property3 = new PropertyReference(subject1Mock.Object, "Prop3");

        manager.ClaimSource(property1);
        manager.ClaimSource(property2);
        manager.ClaimSource(property3);

        // Act - Detach subject1
        lifecycleInterceptor.RaiseSubjectDetached(subject1Mock.Object);

        // Assert
        Assert.Single(detachedSubjects);
        Assert.Equal(subject1Mock.Object, detachedSubjects[0]);
        Assert.Equal(2, releasedProperties.Count); // property1 and property3
        Assert.Contains(property1, releasedProperties);
        Assert.Contains(property3, releasedProperties);
        Assert.Single(manager.Properties); // Only property2 remains
        Assert.Contains(property2, manager.Properties);
    }

    [Fact]
    public void SubscribeToLifecycle_WhenLifecycleNotConfigured_DoesNotThrow()
    {
        // Arrange
        var sourceMock = CreateSourceMock();
        var manager = new SourceOwnershipManager(sourceMock.Object);
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<LifecycleInterceptor>()).Returns((LifecycleInterceptor?)null);

        // Act & Assert - Should not throw
        manager.SubscribeToLifecycle(contextMock.Object);
    }

    [Fact]
    public void SetSource_AfterRelease_AllowsNewClaim()
    {
        // Arrange
        var source1Mock = CreateSourceMock();
        var source2Mock = CreateSourceMock();
        var manager1 = new SourceOwnershipManager(source1Mock.Object);
        var manager2 = new SourceOwnershipManager(source2Mock.Object);
        var property = CreatePropertyReference();

        // First source claims property
        manager1.ClaimSource(property);

        // First source releases property
        manager1.ReleaseSource(property);

        // Act - Second source claims same property
        var result = manager2.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Contains(property, manager2.Properties);
    }

    private static Mock<ISubjectSource> CreateSourceMock()
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        return mock;
    }

    private static PropertyReference CreatePropertyReference(string name = "TestProperty")
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        return new PropertyReference(subjectMock.Object, name);
    }
}

// Extension for testing - expose method to raise event
internal static class LifecycleInterceptorExtensions
{
    public static void RaiseSubjectDetached(this LifecycleInterceptor interceptor, IInterceptorSubject subject)
    {
        // Use reflection to raise the event since it's internal
        var eventField = typeof(LifecycleInterceptor)
            .GetField("SubjectDetached", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        if (eventField?.GetValue(interceptor) is Action<SubjectLifecycleChange> handler)
        {
            handler.Invoke(new SubjectLifecycleChange(subject, null, null, 0));
        }
    }
}
